using System.Collections.Concurrent;
using System.Reflection;
using Framework.Core;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using EntityObj = Framework.Entity.Entity;
using EntityManagerObj = Framework.Entity.EntityManager;

namespace Framework.Scripting;

/// <summary>
/// 脚本宿主（对标 KBE 的 Python 脚本层）：
/// - 从 scripts 目录加载 .csx 脚本文件，编译为程序集并实例化 IEntityScript
/// - 支持热更新：文件变更自动重新编译并替换脚本实例
/// - 脚本与底层框架物理分离：改玩法只改 .csx，无需重新编译框架
/// </summary>
public sealed class ScriptHost : IDisposable
{
    /// <summary>当前活跃的脚本宿主（供脚本类静态访问全局数据）。</summary>
    public static ScriptHost? Current { get; private set; }

    private readonly string scriptsDir;
    private readonly ConcurrentDictionary<string, IEntityScript> scripts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Exception> lastLoadErrors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object?> globalData = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<EntityManagerObj, byte> entityManagers = new();
    private readonly ConcurrentDictionary<long, Action<string, object?, object?>> propertyHandlers = new();
    /// <summary>热更新前透传给 OnReload 的 state 字典（KBE-Gap-Review S4）。</summary>
    private readonly ConcurrentDictionary<string, object?> _reloadStates = new(StringComparer.Ordinal);
    /// <summary>每个类型当前生效的 ScriptVersion（KBE-Gap-Review S4）：热更新时若版本号变更则全量打日志，便于诊断破坏性变更。</summary>
    private readonly ConcurrentDictionary<string, int> _scriptVersions = new(StringComparer.Ordinal);
    /// <summary>每个类型当前生效脚本程序集的可回收加载上下文（P1-5 程序集泄漏修复：热更新后卸载旧 ALC，旧程序集可被 GC 回收）。</summary>
    private readonly ConcurrentDictionary<string, System.Runtime.Loader.AssemblyLoadContext> scriptContexts = new(StringComparer.Ordinal);
    private FileSystemWatcher? watcher;
    private readonly object compileGate = new();

    /// <summary>脚本加载/重载事件（typeName -> 脚本实例）。</summary>
    public event Action<string, IEntityScript>? ScriptLoaded;

    /// <summary>脚本重载事件（typeName, newVersion, oldVersion）：用于宿主记录版本号变更日志/做迁移决策。</summary>
    public event Action<string, int, int>? ScriptReloaded;

    /// <summary>最近一次脚本加载错误（typeName -> 异常；热更新失败时保留旧实例并记录）。</summary>
    public IReadOnlyDictionary<string, Exception> LastLoadErrors => lastLoadErrors;

    /// <summary>
    /// 全局共享数据（对标 KBE KBEngine.globalData）：
    /// 脚本之间通过键值对共享状态（配置、全局开关、跨实体数据），
    /// 框架侧可通过 SetGlobal/GetGlobal 读写。
    /// 写入会触发各脚本的 OnGlobalChanged 回调（事件驱动协作，替代轮询）。
    /// </summary>
    public object? GetGlobal(string key) => globalData.TryGetValue(key, out var v) ? v : null;

    /// <summary>设置全局共享数据（脚本/框架均可调用），并广播 OnGlobalChanged 事件。</summary>
    public void SetGlobal(string key, object? value)
    {
        globalData[key] = value;
        NotifyGlobalChanged(key, value);
    }

    /// <summary>全局数据键集合。</summary>
    public IEnumerable<string> GlobalKeys => globalData.Keys;

    /// <summary>
    /// 注册实体管理器（供全局数据变更通知按类型遍历实体，如 Quest 任务实体）。
    /// 宿主（服务器/测试）在创建实体管理器后调用；同一管理器重复注册无副作用。
    /// </summary>
    public ScriptHost RegisterEntityManager(EntityManagerObj manager)
    {
        entityManagers[manager] = 0;
        return this;
    }

    /// <summary>
    /// 注销实体管理器（场景销毁时调用，防 entityManagers 只增不减的无界泄漏）。
    /// 注销后该管理器中的实体不再收到全局数据变更/热更新通知；幂等。
    /// </summary>
    public ScriptHost UnregisterEntityManager(EntityManagerObj manager)
    {
        entityManagers.TryRemove(manager, out _);
        return this;
    }

    /// <summary>获取指定类型已加载脚本的版本号（KBE-Gap-Review S4）。未加载返回 0。</summary>
    public int GetScriptVersion(string entityType)
        => _scriptVersions.TryGetValue(entityType, out var v) ? v : 0;

    /// <summary>设置指定类型的重载 state（KBE-Gap-Review S4：迁移辅助）。
    /// 旧实例可在 OnReload 钩子里读这个 state 来恢复运行期缓存（如 tickCount、随机种子）。
    /// </summary>
    public void SetReloadState(string entityType, object? state)
        => _reloadStates[entityType] = state;

    public ScriptHost(string scriptsDir)
    {
        this.scriptsDir = Path.GetFullPath(scriptsDir);
    }

    /// <summary>启动：加载全部 .csx 并启用热更新监听。</summary>
    public void Start()
    {
        Current = this;
        Directory.CreateDirectory(scriptsDir);
        LoadAll();

        watcher = new FileSystemWatcher(scriptsDir, "*.csx")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        watcher.Changed += OnScriptFileChanged;
        watcher.Created += OnScriptFileChanged;
        Log.Info($"ScriptHost 启动，脚本目录: {scriptsDir}，已加载脚本: {scripts.Count}");
    }

    /// <summary>重新加载全部脚本（手动触发）。</summary>
    public void ReloadAll() => LoadAll();

    /// <summary>
    /// 暂停 FileSystemWatcher 自动重载（KBE-Gap-Review S4：测试/管理场景下需要"写文件 + ReloadAll" 的一次性操作，
    /// 避免 watcher 防抖窗口内多个事件竞争）。
    /// </summary>
    public void PauseWatcher()
    {
        if (watcher != null) watcher.EnableRaisingEvents = false;
    }

    /// <summary>恢复 FileSystemWatcher。</summary>
    public void ResumeWatcher()
    {
        if (watcher != null) watcher.EnableRaisingEvents = true;
    }

    /// <summary>获取脚本实例；未加载返回 null。</summary>
    public IEntityScript? GetScript(string entityType)
    {
        scripts.TryGetValue(entityType, out var script);
        return script;
    }

    /// <summary>向实体分发 tick 事件（由 TickEngine 驱动；按实体类型索引直达，O(该类型实体数)）。</summary>
    public void TickAll(EntityManagerObj manager, long frame)
    {
        foreach (var script in scripts.Values)
        {
            foreach (var entity in manager.GetAllEntitiesByType(script.EntityType))
            {
                try
                {
                    script.OnTick(entity, frame);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"脚本 {script.EntityType} OnTick 异常 EntityId:{entity.EntityId}");
                }
            }
        }
    }

    /// <summary>向实体分发消息（客户端消息/远程调用）。</summary>
    public bool DispatchMessage(EntityObj entity, string method, object?[] args)
    {
        if (!scripts.TryGetValue(entity.TypeName, out var script))
        {
            return false;
        }
        try
        {
            script.OnMessage(entity, method, args);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"脚本 {script.EntityType} OnMessage 异常 EntityId:{entity.EntityId} Method:{method}");
            return true;
        }
    }

    /// <summary>实体创建时通知脚本（并订阅实体属性变更事件）。</summary>
    public void NotifyCreate(EntityObj entity)
    {
        if (scripts.TryGetValue(entity.TypeName, out _))
        {
            // 安全修复（P1）：回调内按当前注册的脚本实例解析，避免热更新后属性变更仍派发给旧实例。
            Action<string, object?, object?> handler = (name, oldValue, newValue) =>
            {
                try
                {
                    if (scripts.TryGetValue(entity.TypeName, out var currentScript))
                    {
                        currentScript.OnPropertyChanged(entity, name, oldValue, newValue);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"脚本 {entity.TypeName} OnPropertyChanged 异常 EntityId:{entity.EntityId} Prop:{name}");
                }
            };
            propertyHandlers[entity.EntityId] = handler;
            entity.PropertyChanged += handler;
            if (scripts.TryGetValue(entity.TypeName, out var script))
            {
                // OnCreate 保持同步执行（恢复原语义）：服务器在 tick 线程建实体后通常会立即读取
                // 初始化属性（Hp 等）。若一律投递到 tick 线程，OnCreate 延迟一拍执行，会破坏
                // "创建→初始化→立即读取"契约。线程安全由调用方保证（NotifyCreate 在 tick 线程调用）。
                script.OnCreate(entity);
            }
        }
    }

    /// <summary>实体销毁时通知脚本（并退订属性变更事件）。</summary>
    public void NotifyDestroy(EntityObj entity)
    {
        if (propertyHandlers.TryRemove(entity.EntityId, out var handler))
        {
            entity.PropertyChanged -= handler;
        }
        if (scripts.TryGetValue(entity.TypeName, out var script))
        {
            script.OnDestroy(entity);
        }
    }

    /// <summary>
    /// 注入的 TickEngine（KBE-Gap-Review S2：脚本可注册定时器代替 tick%N 轮询）。
    /// 宿主在启动时调用 <see cref="AttachTickEngine"/>；未注入时为 null，脚本里要判空。
    /// </summary>
    public Framework.Tick.TickEngine? TickEngine { get; private set; }

    /// <summary>挂载 TickEngine（Battle/服务器启动时调用）。</summary>
    public void AttachTickEngine(Framework.Tick.TickEngine engine) => TickEngine = engine;

    /// <summary>
    /// 通知热更新（KBE-Gap-Review S4）：新实例的 OnReload 钩子按类型下每个实体各调用一次。
    /// 错误隔离：单实体异常不影响其他实体迁移。
    /// 安全修复（P1）：
    ///   1) 改为调用 newScript.OnReload（迁移钩子必须挂在新的代码实例上），
    ///      否则存量实体出现新旧代码混跑、新实例字段永远为 null。
    ///   2) 跨线程迁移（P1-3 竞争）：LoadScriptFile 由 FSW 线程触发，直接遍历实体调用 OnReload
    ///      会与 Battle tick 线程的实体处理/定时器并发竞争（entity.Get/Set、AddTimer 竞态）。
    ///      这里通过 TickEngine.Post 把整段实体访问投递到 tick 线程串行执行；
    ///      未挂载 TickEngine（启动早期）时回退为直接执行。
    /// </summary>
    private void NotifyReload(string typeName, IEntityScript oldScript, IEntityScript newScript, object? state)
    {
        if (TickEngine != null)
        {
            TickEngine.Post(() => NotifyReloadOnTick(typeName, oldScript, newScript, state));
        }
        else
        {
            NotifyReloadOnTick(typeName, oldScript, newScript, state);
        }
    }

    private void NotifyReloadOnTick(string typeName, IEntityScript oldScript, IEntityScript newScript, object? state)
    {
        // 取消旧实例的全部定时器（P1）：OnReload 挂在 newScript 上后，旧实例的字段级句柄
        // 已不可达（脚本里 healTimer?.Cancel() 操作的是新实例的 null 字段），若不在此取消，
        // 旧定时器会继续运行并与新实例重挂的定时器叠加（回血/掉落速率翻倍）且把旧实例/旧程序集钉在堆上。
        (oldScript as EntityScriptBase)?.CancelAllTimers();

        foreach (var manager in entityManagers.Keys)
        {
            foreach (var entity in manager.GetAllEntitiesByType(typeName))
            {
                try
                {
                    newScript.OnReload(entity, state);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"脚本 {typeName} OnReload 异常 EntityId:{entity.EntityId}");
                }
            }
        }
    }

    /// <summary>
    /// 全局数据变更通知：对每个脚本按其绑定的实体类型直达遍历（类型索引），逐个调用 OnGlobalChanged。
    /// 脚本实例按实体类型共享，因此回调需要实体参数（事件可能影响同类型的多个实体）。
    /// P3 修复（线程感知）：SetGlobal 可能被非 tick 线程（配置重载/后台任务）调用，原实现在调用线程直接
    /// 遍历活动实体集合，与 tick 线程并发竞争。这里 tick 线程内同步（保持脚本内 Get→Set→OnGlobalChanged
    /// 的同步语义），非 tick 线程投递到 tick 线程串行执行。
    /// </summary>
    private void NotifyGlobalChanged(string key, object? value)
    {
        if (TickEngine != null && !TickEngine.IsOnTickThread)
        {
            TickEngine.Post(() => NotifyGlobalChangedOnTick(key, value));
        }
        else
        {
            NotifyGlobalChangedOnTick(key, value);
        }
    }

    private void NotifyGlobalChangedOnTick(string key, object? value)
    {
        foreach (var script in scripts.Values)
        {
            foreach (var manager in entityManagers.Keys)
            {
                foreach (var entity in manager.GetAllEntitiesByType(script.EntityType))
                {
                    try
                    {
                        script.OnGlobalChanged(entity, key, value);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"脚本 {script.EntityType} OnGlobalChanged 异常 EntityId:{entity.EntityId} Key:{key}");
                    }
                }
            }
        }
    }

    private void LoadAll()
    {
        lock (compileGate)
        {
            foreach (var file in Directory.GetFiles(scriptsDir, "*.csx"))
            {
                try
                {
                    LoadScriptFile(file);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"脚本加载失败: {file}");
                }
            }
        }
    }

    private void OnScriptFileChanged(object sender, FileSystemEventArgs e)
    {
        // 防抖：文件写入可能多次触发
        Thread.Sleep(200);
        lock (compileGate)
        {
            try
            {
                LoadScriptFile(e.FullPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"脚本热更新失败: {e.FullPath}");
            }
        }
    }

    /// <summary>
    /// 编译并加载单个 .csx 脚本（错误隔离：编译失败不替换旧实例，保留可用的上一版本）。
    /// 脚本文件约定（对标 KBE 脚本模块）：主体定义脚本类并返回实例，例如：
    ///   public class AvatarScript : EntityScriptBase { ... }
    ///   return new AvatarScript();
    /// 脚本内可通过 ScriptGlobals.Global 访问全局共享数据：
    ///   ScriptGlobals.Global.Set("Key", value); var v = ScriptGlobals.Global.Get("Key");
    /// </summary>
    private void LoadScriptFile(string filePath)
    {
        try
        {
            string code = File.ReadAllText(filePath);

            // 程序集泄漏修复（P1-5）：从 CSharpScript（载入永不回收的默认 ALC）改为
            // CSharpCompilation + 可回收 AssemblyLoadContext，热更新后卸载旧版本程序集。
            // 脚本约定 "return new XxxScript();" 是 CSharpScript 的顶层 return，CSharpCompilation
            // 不支持，改为编译后按类型扫描实例化 IEntityScript。
            string compileSource = StripTrailingReturn(code);
            var syntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(compileSource);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "Script_" + Path.GetFileNameWithoutExtension(filePath),
                new[] { syntaxTree },
                references: GetScriptReferences(),
                options: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release));

            using var emitStream = new MemoryStream();
            var emitResult = compilation.Emit(emitStream);
            if (!emitResult.Success)
            {
                var errors = emitResult.Diagnostics
                    .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    .ToList();
                var ex = new InvalidOperationException($"脚本 {filePath} 编译失败: {errors.Count} 个错误");
                foreach (var err in errors)
                {
                    Log.Error($"  [脚本编译错误] {err}");
                }
                throw ex;
            }

            emitStream.Position = 0;
            var context = new ScriptLoadContext();
            var assembly = context.LoadFromStream(emitStream);

            // 替换原 "return new XxxScript();" 约定：扫描实现 IEntityScript 的具体类型并实例化
            System.Type? scriptType = assembly.GetTypes().FirstOrDefault(t =>
                typeof(IEntityScript).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
            if (scriptType == null)
            {
                Log.Warn($"脚本 {filePath} 未包含 IEntityScript 实现类型（需声明继承 EntityScriptBase 的类），已跳过。");
                return;
            }
            if (Activator.CreateInstance(scriptType) is not IEntityScript instance)
            {
                Log.Warn($"脚本 {filePath} 无法实例化 {scriptType.FullName}，已跳过。");
                return;
            }

            string typeName = instance.EntityType;
            int newVersion = instance.ScriptVersion;
            // KBE-Gap-Review S4：版本号跟踪
            int oldVersion = 0;
            if (scripts.TryGetValue(typeName, out var oldScript))
            {
                oldVersion = (oldScript as IEntityScript)?.ScriptVersion ?? 0;
            }
            if (oldVersion != 0 && oldVersion != newVersion)
            {
                Log.Warn($"脚本 {typeName} 版本号变更 {oldVersion} -> {newVersion}，状态迁移需由 OnReload 钩子显式处理");
            }
            _scriptVersions[typeName] = newVersion;

            scripts[typeName] = instance;
            // 错误簿记键与失败登记保持一致：失败时按文件名登记（编译失败时拿不到类型名），
            // 成功时按文件名清除；同时兼容按类型名登记的旧键（文件名 ≠ 类型名时也能正确清除）。
            var loadedFileName = Path.GetFileNameWithoutExtension(filePath);
            lastLoadErrors.TryRemove(loadedFileName, out _);
            lastLoadErrors.TryRemove(typeName, out _);
            if (oldVersion != 0)
            {
                Log.Info($"脚本热更新成功: {typeName} v{oldVersion} -> v{newVersion} <- {Path.GetFileName(filePath)}");
            }
            else
            {
                Log.Info($"脚本加载成功: {typeName} v{newVersion} <- {Path.GetFileName(filePath)}");
            }

            // KBE-Gap-Review S4：热更新时通知旧实例的 OnReload 钩子（带 state）
            if (oldScript != null && !ReferenceEquals(oldScript, instance))
            {
                _reloadStates.TryGetValue(typeName, out var reloadState);
                NotifyReload(typeName, oldScript, instance, reloadState);
            }

            // 卸载上一版本的可回收 ALC：旧实例此时已无强引用（scripts 已替换、
            // 旧定时器已由 NotifyReload 取消、属性回调按当前实例解析），Unload 后程序集可被 GC 回收。
            if (scriptContexts.TryRemove(typeName, out var oldContext))
            {
                try
                {
                    oldContext.Unload();
                }
                catch (Exception ex)
                {
                    Log.Warn($"脚本 {typeName} 旧程序集卸载发起失败（引用释放后由 GC 自动回收）: {ex.Message}");
                }
            }
            scriptContexts[typeName] = context;

            ScriptLoaded?.Invoke(typeName, instance);
            ScriptReloaded?.Invoke(typeName, newVersion, oldVersion);
        }
        catch (Exception ex)
        {
            // 错误隔离：记录错误但保留旧实例（若有），游戏逻辑继续用上一版本运行
            Log.Error($"脚本加载失败（保留旧实例）: {filePath} Exception:{ex.Message}");
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            lastLoadErrors[fileName] = ex;
        }
    }

    /// <summary>
    /// 去除脚本文件尾部的顶层 "return new XxxScript();"（仅 CSharpScript 支持顶层 return，CSharpCompilation 不支持）。
    /// 仅当该 return 是文件最后一条语句（其后无有效内容）时剥离；若 return 后有尾随内容（如测试注入的非法代码），
    /// 原样返回让编译失败——这是错误隔离的关键：热更新文件若在 return 后混入内容，必须按编译错误处理而非静默丢弃。
    /// </summary>
    private static string StripTrailingReturn(string code)
    {
        string trimmed = code.TrimEnd();
        int idx = trimmed.LastIndexOf("return new ", StringComparison.Ordinal);
        if (idx < 0) return code;
        int semi = trimmed.IndexOf(';', idx);
        if (semi < 0) return code;
        if (trimmed.AsSpan(semi + 1).Trim().Length != 0) return code;
        return trimmed.Substring(0, idx);
    }

    /// <summary>脚本编译引用：运行时已加载的全部非动态程序集（框架 + BCL + Shared），可回收 ALC 经回退默认上下文解析。</summary>
    private static Microsoft.CodeAnalysis.MetadataReference[] GetScriptReferences()
    {
        var refs = new List<Microsoft.CodeAnalysis.MetadataReference>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location))
            {
                continue;
            }
            refs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(asm.Location));
        }
        return refs.ToArray();
    }

    /// <summary>脚本程序集的可回收加载上下文（P1-5）：引用一律回退默认上下文（框架/BCL 已在默认 ALC 中）。</summary>
    private sealed class ScriptLoadContext : System.Runtime.Loader.AssemblyLoadContext
    {
        public ScriptLoadContext() : base(isCollectible: true) { }

        protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName assemblyName) => null;
    }

    public void Dispose()
    {
        watcher?.Dispose();
        watcher = null;
        foreach (var (typeName, context) in scriptContexts)
        {
            if (scriptContexts.TryRemove(typeName, out _))
            {
                try { context.Unload(); }
                catch { /* 引用未释放时由 GC 自动回收 */ }
            }
        }
    }
}

/// <summary>
/// 脚本全局对象（注入到 .csx 脚本作为 globals 参数）：
/// 脚本通过 ScriptGlobals.Global 访问 ScriptHost 的全局共享数据。
/// </summary>
public sealed class ScriptGlobals
{
    /// <summary>脚本宿主（框架注入）。</summary>
    public ScriptHost Host { get; }

    /// <summary>全局共享数据访问器（对标 KBE KBEngine.globalData）。</summary>
    public ScriptGlobalData Global { get; }

    public ScriptGlobals(ScriptHost host)
    {
        Host = host;
        Global = new ScriptGlobalData(host);
    }
}

/// <summary>脚本全局数据访问器。</summary>
public sealed class ScriptGlobalData
{
    private readonly ScriptHost host;

    public ScriptGlobalData(ScriptHost host)
    {
        this.host = host;
    }

    public object? Get(string key) => host.GetGlobal(key);

    public void Set(string key, object? value) => host.SetGlobal(key, value);

    public IEnumerable<string> Keys => host.GlobalKeys;
}

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
    private FileSystemWatcher? watcher;
    private readonly object compileGate = new();

    /// <summary>脚本加载/重载事件（typeName -> 脚本实例）。</summary>
    public event Action<string, IEntityScript>? ScriptLoaded;

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
        if (scripts.TryGetValue(entity.TypeName, out var script))
        {
            Action<string, object?, object?> handler = (name, oldValue, newValue) =>
            {
                try
                {
                    script.OnPropertyChanged(entity, name, oldValue, newValue);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"脚本 {script.EntityType} OnPropertyChanged 异常 EntityId:{entity.EntityId} Prop:{name}");
                }
            };
            propertyHandlers[entity.EntityId] = handler;
            entity.PropertyChanged += handler;
            script.OnCreate(entity);
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
    /// 全局数据变更通知：对每个脚本按其绑定的实体类型直达遍历（类型索引），逐个调用 OnGlobalChanged。
    /// 脚本实例按实体类型共享，因此回调需要实体参数（事件可能影响同类型的多个实体）。
    /// </summary>
    private void NotifyGlobalChanged(string key, object? value)
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
            var options = ScriptOptions.Default
                .WithReferences(
                    typeof(IEntityScript).Assembly,        // Framework.Scripting
                    typeof(EntityObj).Assembly,            // Framework.Entity
                    typeof(Log).Assembly,                  // Framework.Core
                    typeof(object).Assembly)               // System.Private.CoreLib
                .WithImports("System", "System.Collections.Generic", "Framework.Entity", "Framework.Scripting", "Framework.Core");

            var script = CSharpScript.Create<object>(code, options, globalsType: typeof(ScriptGlobals));
            var diagnostics = script.Compile();
            var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                var ex = new InvalidOperationException($"脚本 {filePath} 编译失败: {errors.Count} 个错误");
                foreach (var err in errors)
                {
                    Log.Error($"  [脚本编译错误] {err}");
                }
                throw ex;
            }
            var state = script.RunAsync(new ScriptGlobals(this)).GetAwaiter().GetResult();

            // 脚本约定：主体返回 IEntityScript 实例
            if (state.ReturnValue is not IEntityScript instance)
            {
                Log.Warn($"脚本 {filePath} 未返回 IEntityScript 实例（需以 return new XxxScript(); 结尾），已跳过。");
                return;
            }

            string typeName = instance.EntityType;
            scripts[typeName] = instance;
            // 错误簿记键与失败登记保持一致：失败时按文件名登记（编译失败时拿不到类型名），
            // 成功时按文件名清除；同时兼容按类型名登记的旧键（文件名 ≠ 类型名时也能正确清除）。
            var loadedFileName = Path.GetFileNameWithoutExtension(filePath);
            lastLoadErrors.TryRemove(loadedFileName, out _);
            lastLoadErrors.TryRemove(typeName, out _);
            Log.Info($"脚本加载成功: {typeName} <- {Path.GetFileName(filePath)}");
            ScriptLoaded?.Invoke(typeName, instance);
        }
        catch (Exception ex)
        {
            // 错误隔离：记录错误但保留旧实例（若有），游戏逻辑继续用上一版本运行
            Log.Error($"脚本加载失败（保留旧实例）: {filePath} Exception:{ex.Message}");
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            lastLoadErrors[fileName] = ex;
        }
    }

    public void Dispose()
    {
        watcher?.Dispose();
        watcher = null;
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

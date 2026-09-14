using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ClientProtocol;
using MemoryPack;

// ============================================================================
// ClientGenVerify：验证 ClientGen 生成的客户端编解码与服务器 MemoryPack 逐字节兼容。
// 对每个客户端消息：
//   1. 用反射构造一个带代表性取值的客户端样本（含中文、负数、数组、map、嵌套结构体）
//   2. 按字段名拷贝到服务器真实类（Framework.Protocol.Generated，[MemoryPackable]）
//   3. 用真实 MemoryPack 序列化 -> bytes
//   4. 用客户端 codec 反序列化 bytes -> 断言字段与样本完全一致（读方向）
//   5. 用客户端 codec 序列化样本 -> 断言与 MemoryPack bytes 逐字节一致（写方向）
// 全部通过即证明：客户端可直接与服务器通信（双向）。
// ============================================================================

internal static class Program
{
    private static int pass = 0;
    private static readonly List<string> failures = new();

    private static int Main()
    {
        // 强制加载服务器协议程序集（生成类的宿主）
        Assembly.Load("Framework.Protocol");
        _ = typeof(Framework.Protocol.Generated.Login); // 触达 [MemoryPackable] 生成类

        Console.WriteLine("===== ClientGenVerify：客户端编解码 vs 服务器 MemoryPack 逐字节互验 =====");
        Console.WriteLine();

        var clientTypes = typeof(IMessage).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(IMessage).IsAssignableFrom(t))
            .OrderBy(t => t.Name)
            .ToList();

        // 先跑门禁：产物缺失/多余直接红灯（逐消息互验只遍历“产物里已有的类型”，对缺失天然失明）
        VerifyFreshness(clientTypes);
        // UE 产物编码门禁：无 UTF-8 BOM 会在中文 Windows 的 MSVC 下直接编译不过
        VerifyUeEncoding();

        foreach (var ct in clientTypes)
        {
            VerifyMessage(ct);
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine($"===== 全部验证通过（{pass} 个消息，读写双向逐字节一致）=====");
            return 0;
        }
        Console.WriteLine($"===== 失败 {failures.Count} 项 =====");
        foreach (var f in failures) Console.WriteLine("  FAIL: " + f);
        return 1;
    }

    // ---------- 产物新鲜度门禁 ----------

    /// <summary>
    /// 断言「生成的客户端产物」不落后于协议：ProtocolManifest 里每一条客户端可见消息
    /// （非 internal 且非 target=Db）都必须在产物中有对应消息类，且产物不得多出协议里已不存在的类。
    /// 为何需要：Output/ 是入库交付物，但**没有任何构建/CI 步骤**保证它随协议更新
    /// （实测：入库产物曾长期停留旧协议，缺 BattleSpectate 40012-40015 / Party* 31001-31015 等，
    /// 而逐消息互验只遍历“产物里已有的类型”，对“缺失”天然失明）。
    /// </summary>
    private static void VerifyFreshness(List<Type> clientTypes)
    {
        var byName = new HashSet<string>(clientTypes.Select(t => t.Name), StringComparer.Ordinal);
        var expected = new List<(int Id, string Name)>();

        using var doc = JsonDocument.Parse(Framework.Protocol.Generated.ProtocolManifest.Json);
        foreach (var m in doc.RootElement.GetProperty("messages").EnumerateArray())
        {
            bool isInternal = m.TryGetProperty("internal", out var iv) && iv.ValueKind == JsonValueKind.True;
            string target = m.TryGetProperty("target", out var tv) ? tv.GetString() ?? "" : "";
            if (isInternal || string.Equals(target, "Db", StringComparison.OrdinalIgnoreCase)) continue;
            expected.Add((m.GetProperty("id").GetInt32(), m.GetProperty("name").GetString()!));
        }

        var missing = expected.Where(e => !byName.Contains(e.Name)).Select(e => $"{e.Id} {e.Name}").ToList();
        var expectedNames = new HashSet<string>(expected.Select(e => e.Name), StringComparer.Ordinal);
        var extra = clientTypes.Where(t => !expectedNames.Contains(t.Name)).Select(t => t.Name).ToList();

        if (missing.Count == 0 && extra.Count == 0)
        {
            Console.WriteLine($"  通过  产物新鲜度：客户端可见消息 {expected.Count} 条与产物 {clientTypes.Count} 个类型完全对应");
            return;
        }

        if (missing.Count > 0)
            failures.Add($"产物陈旧：协议有 {missing.Count}/{expected.Count} 条客户端可见消息未出现在产物中 —— {string.Join(", ", missing.Take(12))}（请重新运行 ClientGen 生成 Output/）");
        if (extra.Count > 0)
            failures.Add($"产物有多余类型（协议中已不存在）：{string.Join(", ", extra.Take(12))}");
    }

    /// <summary>
    /// 断言 UE 的 C++ 产物带 UTF-8 BOM。
    /// 为何必须：产物注释是中文（UTF-8 多字节），而 MSVC 在中文 Windows（936 代码页）下会把
    /// **无 BOM** 的 UTF-8 源当 ANSI 解码 → 多字节序列错位可能吞掉后续代码行，
    /// 实测症状是一大片 “error C2039: "ReadCount": 不是 mp::Reader 的成员”（而声明就在类内），
    /// **整个 UE 产物开箱编译不过**，而当时 dotnet build 与全部 8 套测试均全绿。
    /// BOM 是 MSVC 无需命令行开关即可正确识别 UTF-8 的唯一途径（clang/gcc 也接受 BOM）。
    /// 这里只做“没有 BOM 就红灯”的廉价守护；真实编译检查见 Tools/ClientGen/verify-ue-syntax.ps1。
    /// </summary>
    private static void VerifyUeEncoding()
    {
        var dir = FindUeOutputDir();
        if (dir == null)
        {
            failures.Add("找不到 UE 产物目录（预期 <repo>/NetGameServer/Tools/ClientGen/Output/UE）");
            return;
        }

        var files = Directory.GetFiles(dir, "*.h")
            .Concat(Directory.GetFiles(dir, "*.cpp"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0) { failures.Add($"UE 产物目录里没有 .h/.cpp：{dir}"); return; }

        var bad = new List<string>();
        Span<byte> head = stackalloc byte[3];
        foreach (var f in files)
        {
            using var fs = File.OpenRead(f);
            head.Clear();
            int n = fs.Read(head);
            if (n < 3 || head[0] != 0xEF || head[1] != 0xBB || head[2] != 0xBF) bad.Add(Path.GetFileName(f));
        }

        if (bad.Count == 0)
        {
            Console.WriteLine($"  通过  UE 产物编码：{files.Count} 个 .h/.cpp 均带 UTF-8 BOM（MSVC 936 代码页下可正确解析）");
            return;
        }
        failures.Add($"UE 产物缺少 UTF-8 BOM（MSVC 在 936 代码页下会按 ANSI 解码，可能直接编译不过）：{string.Join(", ", bad)}");
    }

    /// <summary>从测试输出目录向上找 ClientGen 的 UE 产物目录（测试运行目录是 bin/Debug/netX，不能用固定相对路径）。</summary>
    private static string? FindUeOutputDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "Tools", "ClientGen", "Output", "UE");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void VerifyMessage(Type clientType)
    {
        object sample;
        try
        {
            sample = Activator.CreateInstance(clientType)!;
            FillSample(sample);
        }
        catch (Exception ex)
        {
            failures.Add($"{clientType.Name} 构造样本失败: {ex.Message}");
            return;
        }

        var msgIdField = clientType.GetField("MsgId");
        int msgId = (int)msgIdField!.GetValue(null)!;
        string serverName = "Framework.Protocol.Generated." + clientType.Name;
        var serverType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => SafeTypes(a))
            .FirstOrDefault(t => t.FullName == serverName);
        if (serverType == null)
        {
            failures.Add($"{clientType.Name} 找不到服务器对应类型 {serverName}");
            return;
        }

        try
        {
            // 1) 拷贝到服务器实例
            object server = Activator.CreateInstance(serverType)!;
            CopyInto(server, sample);

            // 2) 真实 MemoryPack 序列化
            byte[] realBytes = MemoryPackSerializer.Serialize(serverType, server);

            // 3) 客户端反序列化真实字节
            var deserialize = clientType.GetMethod("Deserialize", new[] { typeof(byte[]) })!;
            object client2 = deserialize.Invoke(null, new object[] { realBytes })!;

            // 4) 读方向：字段一致
            if (!AreEqual(sample, client2, clientType.Name))
            {
                failures.Add($"{clientType.Name} 读方向不一致（反序列化结果 ≠ 样本）");
                return;
            }

            // 5) 写方向：逐字节一致
            var serialize = clientType.GetMethod("Serialize", Type.EmptyTypes)!;
            byte[] clientBytes = (byte[])serialize.Invoke(sample, null)!;
            if (!clientBytes.AsSpan().SequenceEqual(realBytes))
            {
                failures.Add($"{clientType.Name} 写方向不一致\n  客户端: {Convert.ToHexString(clientBytes)}\n  MemoryPack: {Convert.ToHexString(realBytes)}");
                return;
            }

            pass++;
            Console.WriteLine($"  通过  {clientType.Name,-28} MsgId={msgId,-6} 负载 {realBytes.Length,4} 字节（读写一致）");
        }
        catch (Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            failures.Add($"{clientType.Name} 异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }

    // ---------- 样本构造 ----------

    private static int stringSeed;
    private static int bytesSeed;
    private static int listSeed;
    private static int dictSeed;
    private static int keySeed;

    /// <summary>
    /// 字典 key 专用样本：永不为 null / 空。
    /// 理由：null key 在 MemoryPack/JSON 语义下都是病态输入，不值得覆盖；
    /// 且去重循环 `k2.Equals(k1)` 对 null 会直接抛 NRE（曾真实触发：构造 CenterUpdateRoomSettings 样本失败）。
    /// </summary>
    private static object MakeKeySample(Type t)
    {
        keySeed++;
        if (t == typeof(string)) return $"key{keySeed}";
        if (t == typeof(byte[])) return new byte[] { (byte)(keySeed & 0xFF) };
        return MakeSample(t);
    }

    private static void FillSample(object obj)
    {
        foreach (var f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            f.SetValue(obj, MakeSample(f.FieldType));
        }
    }

    private static object MakeSample(Type t)
    {
        if (t == typeof(bool)) return true;
        if (t == typeof(int)) return -12345;
        if (t == typeof(long)) return 9876543210123L;
        if (t == typeof(float)) return 1.5f;
        if (t == typeof(string))
        {
            // 覆盖 4 种形态：null（MemoryPack 短标记 -1）/ 空串（仅 1 个 int32）/ 中文 / ascii。
            // 原实现只造中文与 ascii —— null 与空串从未被验证，而这两处正是手写 codec 最易偏离的地方
            // （实测踩到：读 null 串会多读 4 字节的 utf16 长度，导致整包后续字段全部错位）。
            stringSeed++;
            return (stringSeed % 4) switch
            {
                0 => null!,
                1 => "",
                2 => $"中文值{stringSeed}",
                _ => $"ascii-{stringSeed}",
            };
        }
        if (t == typeof(byte[]))
        {
            // 覆盖 null / 空数组 / 含 0 与 250 的非空数组（原实现只有后者）
            bytesSeed++;
            return (bytesSeed % 3) switch
            {
                0 => null!,
                1 => Array.Empty<byte>(),
                _ => new byte[] { 1, 2, 3, 250, 0 },
            };
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var et = t.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(t)!;
            int count = (listSeed++ % 3) == 0 ? 0 : 2;   // 空集合也要覆盖（原实现恒为 2 个元素）
            for (int i = 0; i < count; i++) list.Add(MakeSample(et));
            return list;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var kt = t.GetGenericArguments()[0];
            var vt = t.GetGenericArguments()[1];
            var dict = (IDictionary)Activator.CreateInstance(t)!;
            if ((dictSeed++ % 3) == 0) return dict;      // 空字典也覆盖
            var k1 = MakeKeySample(kt);
            var k2 = MakeKeySample(kt);
            while (Equals(k2, k1)) k2 = MakeKeySample(kt);   // 用静态 Equals 避免对 null 抛 NRE
            dict[k1] = MakeSample(vt);
            dict[k2] = MakeSample(vt);
            return dict;
        }
        if (!t.IsValueType)
        {
            var o = Activator.CreateInstance(t)!;
            FillSample(o);
            return o;
        }
        return Activator.CreateInstance(t)!;
    }

    // ---------- 拷贝（字段名匹配，递归） ----------

    private static void CopyInto(object target, object source)
    {
        foreach (var sf in source.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var tp = target.GetType().GetProperty(sf.Name);
            var tf = target.GetType().GetField(sf.Name);
            if (tp != null && tp.CanWrite)
            {
                tp.SetValue(target, CopyValue(tp.PropertyType, sf.GetValue(source)));
            }
            else if (tf != null)
            {
                tf.SetValue(target, CopyValue(tf.FieldType, sf.GetValue(source)));
            }
        }
    }

    private static object? CopyValue(Type targetType, object? sourceValue)
    {
        if (sourceValue == null) return null;
        if (targetType == typeof(string) || targetType == typeof(byte[])) return sourceValue;
        if (targetType.IsPrimitive || targetType.IsEnum) return sourceValue;
        if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
        {
            var et = targetType.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(targetType)!;
            foreach (var item in (IEnumerable)sourceValue) list.Add(CopyValue(et, item));
            return list;
        }
        if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var args = targetType.GetGenericArguments();
            var dict = (IDictionary)Activator.CreateInstance(targetType)!;
            foreach (DictionaryEntry e in (IDictionary)sourceValue)
            {
                dict[CopyValue(args[0], e.Key)!] = CopyValue(args[1], e.Value);
            }
            return dict;
        }
        if (!targetType.IsValueType)
        {
            var o = Activator.CreateInstance(targetType)!;
            CopyInto(o, sourceValue);
            return o;
        }
        return sourceValue;
    }

    // ---------- 深比较 ----------

    /// <summary>空值判定：null / 空串 / 空数组 / 空集合。客户端不区分 null 与空值，故比较时等价。</summary>
    private static bool IsEmptyValue(object? v)
    {
        if (v == null) return true;
        if (v is string s) return s.Length == 0;
        if (v is byte[] b) return b.Length == 0;
        if (v is ICollection c) return c.Count == 0;
        return false;
    }

    private static bool AreEqual(object? a, object? b, string path)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null)
        {
            // 客户端把 null 归一化为空值（字段声明为非空 string / 非空集合，见 codec 注释），
            // 故此处把 null 与空值视为等价。null 的**字节编码**不会被这条规则放过：
            // 写方向仍按“客户端字节 == 真实 MemoryPack 字节”严格断言（null 必须写成 -1）。
            return IsEmptyValue(a) && IsEmptyValue(b);
        }
        if (a.GetType() != b.GetType())
        {
            // 客户端字段类型与反序列化自同一 codec，应一致；直接比较值
        }
        if (a is byte[] ab && b is byte[] bb) return ab.AsSpan().SequenceEqual(bb);
        if (a is string || a.GetType().IsPrimitive) return a.Equals(b);
        if (a is IEnumerable ea && b is IEnumerable eb)
        {
            var la = ea.Cast<object?>().ToList();
            var lb = eb.Cast<object?>().ToList();
            if (la.Count != lb.Count) return false;
            for (int i = 0; i < la.Count; i++)
            {
                if (!AreEqual(la[i], lb[i], $"{path}[{i}]")) return false;
            }
            return true;
        }
        foreach (var f in a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var bf = b.GetType().GetField(f.Name);
            if (bf == null) return false;
            if (!AreEqual(f.GetValue(a), bf.GetValue(b), $"{path}.{f.Name}")) return false;
        }
        return true;
    }
}

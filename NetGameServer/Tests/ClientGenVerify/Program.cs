using System.Collections;
using System.Reflection;
using System.Text;
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
            stringSeed++;
            return stringSeed % 2 == 0 ? $"中文值{stringSeed}" : $"ascii-{stringSeed}";
        }
        if (t == typeof(byte[])) return new byte[] { 1, 2, 3, 250, 0 };
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var et = t.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(t)!;
            for (int i = 0; i < 2; i++) list.Add(MakeSample(et));
            return list;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var kt = t.GetGenericArguments()[0];
            var vt = t.GetGenericArguments()[1];
            var dict = (IDictionary)Activator.CreateInstance(t)!;
            var k1 = MakeSample(kt);
            var k2 = MakeSample(kt);
            while (k2.Equals(k1)) k2 = MakeSample(kt);
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
                dict[CopyValue(args[0], e.Key)] = CopyValue(args[1], e.Value);
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

    private static bool AreEqual(object? a, object? b, string path)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return a == null && b == null;
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

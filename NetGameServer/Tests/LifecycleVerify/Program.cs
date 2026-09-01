using System.Net;
using System.Net.Sockets;
using System.Text;
using Framework.Entity;
using Shared;

// ===== LifecycleVerify（迭代 21）：健康检查 / 优雅关闭 / 可插拔持久化 + 批量落库 =====
// 运行: dotnet run --project Tests/LifecycleVerify -c Release
// 退出码 0 = 全部通过；非 0 = 失败项数。

int failures = 0;
void Check(bool cond, string name)
{
    Console.WriteLine((cond ? "[PASS] " : "[FAIL] ") + name);
    if (!cond) failures++;
}

// ---- 1. 可插拔持久化：内存假存储（验证 IEntityPersistenceStore 接口抽象） ----
var fake = new FakeStore();
var flushDef = new EntityDef { Name = "Player" }
    .Add("Hp", EntityPropertyType.Int32)
    .Add("Nickname", EntityPropertyType.String);
var mgr = new EntityManager();
var service = new EntityPersistenceService(fake, id => flushDef.CreateEntity(id), flushIntervalMs: 1, flushBatchSize: 64);
service.AttachManager(mgr);

var e1 = flushDef.CreateEntity(1);
e1.Set("Hp", 100);
e1.Set("Nickname", "Alice");
mgr.AddOrUpdateEntity(1, e1);
Check(e1.IsPersistDirty, "Entity.Set 后 IsPersistDirty=true（脏状态自动保存前置）");
Check(fake.SaveCount == 0, "未触发 flush 前存储无写入");

await service.FlushDirtyAsync();
Check(fake.SaveCount == 1, $"批量落库写入 1 条（实际 {fake.SaveCount}）");
Check(!e1.IsPersistDirty, "flush 后 IsPersistDirty=false");

var loaded = service.LoadEntityById("Player", 1);
Check(loaded?.Get<int>("Hp") == 100 && loaded?.Get<string>("Nickname") == "Alice", "批量落库后可单条加载恢复");
Check(service.Count("Player") == 1, "存储计数 = 1");
Check(service.StoreName == "Fake", $"StoreName 透传（实际 {service.StoreName}）");

// SaveEntity 立即落库 + 清脏
var e2 = flushDef.CreateEntity(2);
e2.Set("Hp", 50);
mgr.AddOrUpdateEntity(2, e2);
service.SaveEntity(e2);
Check(fake.SaveCount == 2 && !e2.IsPersistDirty, "SaveEntity 立即落库并清脏");

// 配置工厂：File 后端可选（集成路径冒烟）
var opts = new Framework.Persistence.EntityPersistenceOptions
{
    Provider = "File",
    Directory = Path.Combine(Path.GetTempPath(), $"lifecycle_file_{Guid.NewGuid():N}")
};
using (var fileStore = Framework.Persistence.PersistenceStoreFactory.Create(opts))
{
    Check(fileStore is FileEntityPersistenceStore, $"配置工厂创建 File 后端（实际 {fileStore.GetType().Name}）");
}
try
{
    _ = Framework.Persistence.PersistenceStoreFactory.Create(new Framework.Persistence.EntityPersistenceOptions { Provider = "Bogus" });
    Check(false, "未知 Provider 应抛异常（fail-fast）");
}
catch (ArgumentException)
{
    Check(true, "未知 Provider 抛 ArgumentException（fail-fast）");
}

// ---- 2. 健康检查服务 ----
int hp = FreePort();
using var health = HealthServer.Start(hp, "lifecycle-test");
await Task.Delay(150);
var (s1, _) = await HttpGetAsync(hp, "/healthz");
Check(s1 == 200, $"GET /healthz = 200（实际 {s1}）");
var (s2, b2) = await HttpGetAsync(hp, "/readyz");
Check(s2 == 200 && b2.Contains("\"status\":\"ready\""), $"GET /readyz = 200 ready（实际 {s2}）");
var (s3, _) = await HttpGetAsync(hp, "/nope");
Check(s3 == 404, $"GET /nope = 404（实际 {s3}）");

// ---- 3. 优雅关闭（排空：/readyz 应转 503，钩子应执行且幂等） ----
bool hookRan = false;
NodeLifecycle.Default.RegisterShutdownHook(() => { hookRan = true; return Task.CompletedTask; });
await NodeLifecycle.Default.RunShutdownAsync();
Check(NodeLifecycle.Default.IsDraining, "关闭后 IsDraining=true");
Check(hookRan, "关闭钩子已执行");
await NodeLifecycle.Default.RunShutdownAsync();
Check(hookRan, "重复 RunShutdownAsync 幂等（钩子只执行一次）");
var (s4, b4) = await HttpGetAsync(hp, "/readyz");
Check(s4 == 503 && b4.Contains("draining"), $"排空后 GET /readyz = 503（实际 {s4}）");

Console.WriteLine(failures == 0 ? "LifecycleVerify 全部通过" : $"LifecycleVerify 失败 {failures} 项");
return failures == 0 ? 0 : 1;

// ---- helpers ----
int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    int p = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}

async Task<(int Status, string Body)> HttpGetAsync(int port, string path)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    var stream = client.GetStream();
    string req = $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n";
    var bytes = Encoding.ASCII.GetBytes(req);
    await stream.WriteAsync(bytes);
    var sb = new StringBuilder();
    var buf = new byte[4096];
    int n;
    while ((n = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
    {
        sb.Append(Encoding.UTF8.GetString(buf, 0, n));
    }
    string resp = sb.ToString();
    string statusLine = resp.Split('\n')[0];
    int status = statusLine.Length >= 12 ? int.Parse(statusLine.Substring(9, 3)) : 0;
    int hdrEnd = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    string body = hdrEnd >= 0 ? resp[(hdrEnd + 4)..] : "";
    return (status, body);
}

sealed class FakeStore : IEntityPersistenceStore
{
    public int SaveCount;
    private readonly Dictionary<string, byte[]> data = new(StringComparer.Ordinal);

    public string Name => "Fake";

    public void Save(string entityType, long entityId, byte[] props)
    {
        SaveCount++;
        data[$"{entityType}:{entityId}"] = props;
    }

    public byte[]? TryLoad(string entityType, long entityId)
        => data.TryGetValue($"{entityType}:{entityId}", out var p) ? p : null;

    public void Delete(string entityType, long entityId) => data.Remove($"{entityType}:{entityId}");

    public IEnumerable<StoredEntity> LoadAll(string entityType)
        => data
            .Where(kv => kv.Key.StartsWith(entityType + ":", StringComparison.Ordinal))
            .Select(kv => new StoredEntity(long.Parse(kv.Key[(entityType.Length + 1)..]), kv.Value));

    public int Count(string entityType) => data.Count(kv => kv.Key.StartsWith(entityType + ":", StringComparison.Ordinal));

    public void Dispose()
    {
    }
}

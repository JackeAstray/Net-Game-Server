using Framework.Protocol;
using Framework.Protocol.Generated;
using Framework.Core.Security;
using System.Text;

// ===== 1. 消息序列化 round-trip 验证 =====
var loginReq = new Login
{
    Account = "TestUser",
    Password = "secret123",
};

byte[] packet = ProtocolCodec.Encode(loginReq);
Console.WriteLine($"Login 序列化包大小: {packet.Length} bytes (MsgId={Login.MsgId})");

// 模拟 LengthPrefixedPacketReader 剥离长度帧后，剩余为 [MsgId(4)][Body]
bool ok = ProtocolCodec.TryParseFrame(packet.AsSpan(4), out int msgId, out var payload);
Console.WriteLine($"解析帧: ok={ok} MsgId={msgId} PayloadLen={payload.Length}");

var decoded = ProtocolCodec.Decode<Login>(payload.Span);
Console.WriteLine($"反序列化: Account={decoded?.Account} Password={decoded?.Password}");

if (decoded?.Account != "TestUser" || decoded.Password != "secret123")
{
    Console.WriteLine("!! 登录消息 round-trip 失败");
    return 1;
}

// 复杂嵌套结构验证
var joinReq = new BattleJoin
{
    RoomId = "Room_1",
    MaxPlayers = 10,
    CustomRules = new() { ["mode"] = "deathmatch", ["time"] = "300" }
};
byte[] joinPacket = ProtocolCodec.Encode(joinReq);
ProtocolCodec.TryParseFrame(joinPacket.AsSpan(4), out int joinMsgId, out var joinPayload);
var joinDecoded = ProtocolCodec.Decode<BattleJoin>(joinPayload.Span);
if (joinDecoded?.RoomId != "Room_1" || joinDecoded.MaxPlayers != 10 || joinDecoded.CustomRules["mode"] != "deathmatch")
{
    Console.WriteLine("!! BattleJoin 嵌套结构 round-trip 失败");
    return 1;
}
Console.WriteLine($"BattleJoin 嵌套结构 round-trip OK (包大小 {joinPacket.Length} bytes)");

// ===== 2. 路由表验证 =====
string? target = RouterTable.GetTargetServer(10001);
Console.WriteLine($"MsgId 10001 -> {target} (期望 Login)");
if (target != "Login") return 1;

string? targetBattle = RouterTable.GetTargetServer(40001);
Console.WriteLine($"MsgId 40001 -> {targetBattle} (期望 Battle)");
if (targetBattle != "Battle") return 1;

var route = RouterTable.Routes[90999];
Console.WriteLine($"MsgId 90999 InternalAuth -> {route.TargetServer} IsInternal={route.IsInternal}");

// ===== 3. Token 服务验证 =====
var tokenService = new TokenService("test-secret-key-for-verification");
string token = tokenService.Issue(12345, "100000001");
Console.WriteLine($"Token: {token}");

var verified = tokenService.Verify(token);
if (verified == null || verified.Value.UserId != 12345 || verified.Value.Uid != "100000001")
{
    Console.WriteLine("!! Token 验证失败");
    return 1;
}
Console.WriteLine($"Token 验证成功: UserId={verified.Value.UserId} Uid={verified.Value.Uid} Expires={verified.Value.Expires}");

// 篡改 token 应失败
string tampered = token + "x";
if (tokenService.Verify(tampered) != null)
{
    Console.WriteLine("!! 篡改 Token 未被拒绝");
    return 1;
}
Console.WriteLine("篡改 Token 已被拒绝 OK");

// 过期 token 应失败
var expiredService = new TokenService("test-secret-key-for-verification");
string expiredToken = expiredService.Issue(1, "2", TimeSpan.FromSeconds(-10));
if (expiredService.Verify(expiredToken) != null)
{
    Console.WriteLine("!! 过期 Token 未被拒绝");
    return 1;
}
Console.WriteLine("过期 Token 已被拒绝 OK");

// ===== 4. 内部认证验证 =====
var serverFilter = new InternalAuthFilter("shared-secret", "Center-127.0.0.1:31306");
var clientFilter = new InternalAuthFilter("shared-secret", "Gateway-127.0.0.1:31300");
byte[] authPacket = clientFilter.BuildAuthPacket();
int authMsgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(authPacket.AsSpan(0, 4));
if (authMsgId != InternalAuthFilter.AuthMsgId)
{
    Console.WriteLine("!! 认证包 MsgId 错误");
    return 1;
}
bool authed = serverFilter.TryAuthenticate(authPacket.AsSpan(4));
Console.WriteLine($"内部认证: {authed} (期望 True)");
if (!authed || !serverFilter.IsAuthenticated) return 1;

// 错误密钥应失败
var wrongServer = new InternalAuthFilter("wrong-secret", "Center-127.0.0.1:31306");
bool wrongAuthed = wrongServer.TryAuthenticate(authPacket.AsSpan(4));
Console.WriteLine($"错误密钥认证: {wrongAuthed} (期望 False)");
if (wrongAuthed) return 1;

// ===== 5. 实体框架：脏标记 + 属性增量编解码验证 =====
var playerDef = new Framework.Entity.EntityDef { Name = "Player" }
    .Add("Nickname", Framework.Entity.EntityPropertyType.String)
    .Add("Position", Framework.Entity.EntityPropertyType.Float3)
    .Add("Hp", Framework.Entity.EntityPropertyType.Int32)
    .Add("Score", Framework.Entity.EntityPropertyType.Int32)
    .Add("Equipment", Framework.Entity.EntityPropertyType.Int32List);

var playerA = playerDef.CreateEntity(1001);
playerA.Set("Nickname", "TestPlayer");
playerA.Set("Position", new Framework.Entity.Float3(10, 0, 20));
playerA.Set("Hp", 100);
playerA.Set("Equipment", new List<int> { 1, 2, 3 });

var playerB = playerDef.CreateEntity(2001);
Framework.Entity.PropertyCodec.DeserializeInto(playerB, Framework.Entity.PropertyCodec.SerializeAll(playerA), applyDirty: false);
Console.WriteLine($"实体全量同步: Nickname={playerB.Get<string>("Nickname")} Hp={playerB.Get<int>("Hp")} Pos={playerB.Get<Framework.Entity.Float3>("Position")} EquipCount={playerB.Get<List<int>>("Equipment").Count}");
if (playerB.Get<string>("Nickname") != "TestPlayer" || playerB.Get<int>("Hp") != 100) return 1;
// 全量初始化不应标记脏
if (playerB.IsDirty) { Console.WriteLine("!! 全量初始化不应标记脏"); return 1; }

// 增量同步：只修改 Hp 与 Score，脏集合应只含这两个
playerB.Set("Hp", 80);
playerB.Set("Score", 50);
string[] dirty = playerB.TakeDirtyProperties();
Console.WriteLine($"脏属性: [{string.Join(",", dirty)}] (期望 Hp,Score)");
if (dirty.Length != 2 || !dirty.Contains("Hp") || !dirty.Contains("Score")) return 1;

// 增量应用到另一实体
var playerC = playerDef.CreateEntity(3001);
Framework.Entity.PropertyCodec.DeserializeInto(playerC, Framework.Entity.PropertyCodec.SerializeAll(playerA), applyDirty: false);
Framework.Entity.PropertyCodec.DeserializeInto(playerC, Framework.Entity.PropertyCodec.SerializeChanges(playerB, dirty));
Console.WriteLine($"增量同步: Hp={playerC.Get<int>("Hp")} Score={playerC.Get<int>("Score")} (期望 80,50)");
if (playerC.Get<int>("Hp") != 80 || playerC.Get<int>("Score") != 50) return 1;
// 未修改的属性应保持原值
Console.WriteLine($"未变更属性保持: Nickname={playerC.Get<string>("Nickname")} (期望 TestPlayer)");
if (playerC.Get<string>("Nickname") != "TestPlayer") return 1;
// 增量应用应标记脏（供 Witness 广播）；取走后应清空
Console.WriteLine($"增量后脏标记: {playerC.IsDirty} (期望 True)");
if (!playerC.IsDirty) return 1;
var dirtyAfterDelta = playerC.TakeDirtyProperties();
Console.WriteLine($"增量后脏属性: [{string.Join(",", dirtyAfterDelta)}]");
if (playerC.IsDirty) { Console.WriteLine("!! TakeDirtyProperties 后应清空"); return 1; }
// 未声明属性写入应被忽略（不标记脏）
playerC.Set("NotDeclared", 123);
if (playerC.IsDirty) { Console.WriteLine("!! 未声明属性不应标记脏"); return 1; }

// ===== 6. TickEngine 验证 =====
var tickEngine = new Framework.Tick.TickEngine(50); // 50Hz
long tickCount = 0;
int timerFires = 0;
tickEngine.OnTick += _ => Interlocked.Increment(ref tickCount);
var timer = tickEngine.AddTimer(100, () => Interlocked.Increment(ref timerFires), repeat: true);
tickEngine.Start();
Thread.Sleep(500);
tickEngine.Stop();
Console.WriteLine($"TickEngine 50Hz 运行 500ms: tickCount={tickCount} timerFires={timerFires}");
if (tickCount < 15 || timerFires < 2) { Console.WriteLine("!! TickEngine 帧率或定时器异常"); return 1; }

// 取消定时器
var cancelTimer = tickEngine.AddTimer(50, () => { }, repeat: true);
cancelTimer.Cancel();
tickEngine.Start();
Thread.Sleep(150);
tickEngine.Stop();
Console.WriteLine("定时器取消 OK");

// ===== 7. 生成消息增量同步（EntityDeltaSync）验证 =====
var delta = new Framework.Protocol.Generated.EntityDeltaSync
{
    EntityId = 1001,
    Props = Framework.Entity.PropertyCodec.SerializeChanges(playerB, dirty)
};
byte[] deltaPacket = Framework.Protocol.ProtocolCodec.Encode(delta);
Framework.Protocol.ProtocolCodec.TryParseFrame(deltaPacket.AsSpan(4), out int deltaMsgId, out var deltaPayload);
var deltaDecoded = Framework.Protocol.ProtocolCodec.Decode<Framework.Protocol.Generated.EntityDeltaSync>(deltaPayload.Span);
Console.WriteLine($"EntityDeltaSync round-trip: EntityId={deltaDecoded?.EntityId} PropsLen={deltaDecoded?.Props.Length} MsgId={deltaMsgId} (期望 40105)");
if (deltaDecoded?.EntityId != 1001 || deltaMsgId != 40105) return 1;

// ===== 8. KCP 端到端验证（KcpServer <-> KcpClientWrapper 回环） =====
int kcpPort = 42900;

var kcpServer = new Network.Kcp.KcpServer();
int kcpServerReceived = 0;
var kcpReceivedData = new List<byte[]>();
Network.ISession? kcpServerSession = null;
kcpServer.OnSessionConnected += session => kcpServerSession = session;
kcpServer.OnDataReceived += (session, data) =>
{
    Interlocked.Increment(ref kcpServerReceived);
    kcpReceivedData.Add(data.ToArray());
};
await kcpServer.StartAsync(kcpPort);

var kcpClient = new Network.Kcp.KcpClientWrapper("127.0.0.1", kcpPort);
int kcpClientReceived = 0;
kcpClient.OnDataReceived += (session, data) => Interlocked.Increment(ref kcpClientReceived);
await kcpClient.ConnectAsync();

// 客户端发 10 条消息，服务端应全部按序收到
for (int i = 0; i < 10; i++)
{
    kcpClient.Send(Encoding.UTF8.GetBytes($"kcp-msg-{i}"));
    await Task.Delay(30);
}

// 服务端回发（通过 KcpServer 会话）
await Task.Delay(500);
if (kcpServerSession != null)
{
    for (int i = 0; i < kcpReceivedData.Count; i++)
    {
        kcpServerSession.Send(kcpReceivedData[i]);
        await Task.Delay(30);
    }
}

await Task.Delay(800);
Console.WriteLine($"KCP 端到端: 服务端收到={kcpServerReceived} (期望 10) 客户端收到={kcpClientReceived} (期望 10)");
if (kcpServerReceived != 10 || kcpClientReceived != 10)
{
    Console.WriteLine("!! KCP 端到端验证失败");
    kcpClient.Stop();
    await kcpServer.StopAsync();
    return 1;
}
// 验证顺序与内容
for (int i = 0; i < 10; i++)
{
    string expected = $"kcp-msg-{i}";
    string actual = Encoding.UTF8.GetString(kcpReceivedData[i]);
    if (actual != expected)
    {
        Console.WriteLine($"!! KCP 消息乱序或丢失: 期望 {expected} 实际 {actual}");
        kcpClient.Stop();
        await kcpServer.StopAsync();
        return 1;
    }
}
Console.WriteLine("KCP 消息顺序与内容 OK");
kcpClient.Stop();
await kcpServer.StopAsync();

// ===== 9. OrderedTaskQueue 保序验证 =====
var taskQueue = new Framework.Core.OrderedTaskQueue("test");
var executionLog = new List<string>();
var taskResults = new List<Task>();
var gate = new object();
for (int i = 0; i < 20; i++)
{
    int captured = i;
    taskResults.Add(taskQueue.Enqueue($"key-{captured % 4}", () =>
    {
        lock (gate) executionLog.Add($"key-{captured % 4}:{captured}");
    }));
}
await Task.WhenAll(taskResults);

// 同一 key 内顺序必须严格递增
bool ordered = true;
for (int key = 0; key < 4; key++)
{
    int last = -1;
    foreach (var entry in executionLog.Where(e => e.StartsWith($"key-{key}:")))
    {
        int seq = int.Parse(entry.Split(':')[1]);
        if (seq <= last) { ordered = false; break; }
        last = seq;
    }
}
Console.WriteLine($"OrderedTaskQueue: 任务数={executionLog.Count} 同 key 保序={ordered} (期望 20/True)");
if (executionLog.Count != 20 || !ordered) return 1;

// ===== 10. 路由元数据兼容性（新二进制格式 + 旧 JSON 格式双通） =====
// 新格式：Gateway Attach（二进制尾部块）→ 后端 TryExtract
byte[] jsonBody = System.Text.Encoding.UTF8.GetBytes("{\"a\":1}");
byte[] attached = Shared.RouteMetadata.AttachClientSessionId(jsonBody, 555);
byte[] attachedWithTarget = Shared.RouteMetadata.AttachTargetSessionId(attached, 777);
bool okSession = Shared.RouteMetadata.TryExtractClientSessionId(attachedWithTarget, out long sessionId, out var clean1);
bool okTarget = Shared.RouteMetadata.TryExtractTargetSessionId(clean1, out long targetId, out var clean2);
string cleanJson = System.Text.Encoding.UTF8.GetString(clean2);
Console.WriteLine($"二进制元数据: session={sessionId} target={targetId} body={cleanJson} (期望 555/777/{{a:1}})");
if (!okSession || !okTarget || sessionId != 555 || targetId != 777 || cleanJson != "{\"a\":1}") return 1;

// 二进制 String/Bool 字段（uid/nickname/broadcast）
byte[] withUid = Shared.RouteMetadata.AttachUid(clean2, "100000001");
byte[] withNickname = Shared.RouteMetadata.AttachNickname(withUid, "测试昵称");
byte[] withBroadcast = Shared.RouteMetadata.AttachBroadcast(withNickname, true);
bool okUid = Shared.RouteMetadata.TryExtractUid(withBroadcast, out string uid, out var cleanUid);
bool okNickname = Shared.RouteMetadata.TryExtractNickname(cleanUid, out string nickname, out var cleanNick);
bool okBroadcast = Shared.RouteMetadata.TryExtractBroadcast(cleanNick, out bool broadcast, out _);
Console.WriteLine($"二进制字符串字段: uid={uid} nickname={nickname} broadcast={broadcast} (期望 100000001/测试昵称/True)");
if (!okUid || !okNickname || !okBroadcast || uid != "100000001" || nickname != "测试昵称" || !broadcast) return 1;

// 旧格式兼容：纯 JSON 内嵌字段（旧客户端/旧服务器路径）仍可提取
byte[] legacyJson = System.Text.Encoding.UTF8.GetBytes("{\"__clientSessionId\":888,\"a\":1}");
bool okLegacy = Shared.RouteMetadata.TryExtractClientSessionId(legacyJson, out long legacySession, out var legacyClean);
Console.WriteLine($"旧 JSON 格式: session={legacySession} body={System.Text.Encoding.UTF8.GetString(legacyClean)} (期望 888/{{a:1}})");
if (!okLegacy || legacySession != 888) return 1;

// ===== 11. EntityCall 跨进程实体调用验证（本地回环） =====
var callManager = new Framework.Entity.EntityManager();
var targetEntity = callManager.GetEntity(0);
var callDef = new Framework.Entity.EntityDef { Name = "Player" }
    .Add("Hp", Framework.Entity.EntityPropertyType.Int32);
var callEntity = callDef.CreateEntity(9001);
callManager.AddOrUpdateEntity(9001, callEntity);
callEntity.RegisterMethod("AddScore", args =>
{
    int delta = args.Length > 0 && args[0] is int d ? d : 0;
    callEntity.Set("Hp", callEntity.Get<int>("Hp") + delta);
    return callEntity.Get<int>("Hp");
});

// 本地 EntityCall（同节点直接执行）
var localCall = Framework.Entity.EntityCall.Local(9001, callManager);
localCall.Call("AddScore", 25);
Console.WriteLine($"EntityCall 本地调用: Hp={callEntity.Get<int>("Hp")} (期望 25)");
if (callEntity.Get<int>("Hp") != 25) return 1;

// 模拟跨进程：发送方序列化 EntityRemoteCall 消息 → 接收方 DispatchRemoteCall
var callMsg = new Framework.Protocol.Generated.EntityRemoteCall
{
    TargetNodeId = "Battle-test",
    EntityId = 9001,
    MethodName = "AddScore",
    Args = Framework.Entity.ArgCodec.Serialize(new object?[] { 30 })
};
var (handled, result) = callManager.DispatchRemoteCall(callMsg);
Console.WriteLine($"EntityCall 消息分发: handled={handled} result={result} Hp={callEntity.Get<int>("Hp")} (期望 True/55/55)");
if (!handled || (int?)result != 55 || callEntity.Get<int>("Hp") != 55) return 1;

// 参数 round-trip（多种类型）
byte[] argsBytes = Framework.Entity.ArgCodec.Serialize(new object?[] { 1, "hello", 3.14f, true, new Framework.Entity.Float3(1, 2, 3), null, 99L });
object?[] decodedArgs = Framework.Entity.ArgCodec.Deserialize(argsBytes);
Console.WriteLine($"EntityCall 参数编解码: count={decodedArgs.Length} [{(int)decodedArgs[0]},{(string)decodedArgs[1]},{(float)decodedArgs[2]},{ (bool)decodedArgs[3]},{(Framework.Entity.Float3)decodedArgs[4]},null,{(long)decodedArgs[6]}]");
if (decodedArgs.Length != 7 || (int)decodedArgs[0] != 1 || (string)decodedArgs[1] != "hello"
    || (float)decodedArgs[2] != 3.14f || !(bool)decodedArgs[3] || decodedArgs[5] != null || (long)decodedArgs[6] != 99L) return 1;

// ===== 12. MessageDispatcher 配置化分发验证（RouterTable 驱动） =====
var dispatcher = new Framework.Protocol.MessageDispatcher();
int loginHandled = 0;
int battleHandled = 0;
string? lastAccount = null;

dispatcher.RegisterSync<Framework.Protocol.Generated.Login>((ctx, msg) =>
{
    loginHandled++;
    lastAccount = msg.Account;
    ctx.Send(new Framework.Protocol.Generated.LoginResult { Success = true, Message = "ok" });
});
dispatcher.RegisterSync<Framework.Protocol.Generated.BattleJoin>((ctx, msg) =>
{
    battleHandled++;
    ctx.Send(new Framework.Protocol.Generated.BattleJoinResult { Success = true, Message = $"joined {msg.RoomId}" });
});

// 构造会话上下文（记录发出的消息）
var sentMessages = new List<(int msgId, byte[] payload)>();
var testCtx = new TestSessionContext(sentMessages);

// 用 ProtocolCodec 编码消息后经 Dispatcher 分发（模拟网关→业务服路径）
var loginMsg = new Framework.Protocol.Generated.Login { Account = "alice", Password = "pw" };
byte[] loginPacket = Framework.Protocol.ProtocolCodec.Encode(loginMsg);
Framework.Protocol.ProtocolCodec.TryParseFrame(loginPacket.AsSpan(4), out int loginMsgId, out var loginBody);
bool dispatchedLogin = await dispatcher.TryDispatch(testCtx, loginMsgId, loginBody);
Console.WriteLine($"Dispatcher 分发 Login: ok={dispatchedLogin} handled={loginHandled} account={lastAccount} sent={sentMessages.Count} (期望 True/1/alice/1)");
if (!dispatchedLogin || loginHandled != 1 || lastAccount != "alice" || sentMessages.Count != 1) return 1;

var joinMsg2 = new Framework.Protocol.Generated.BattleJoin { RoomId = "Room_9", MaxPlayers = 10 };
byte[] joinPacket2 = Framework.Protocol.ProtocolCodec.Encode(joinMsg2);
Framework.Protocol.ProtocolCodec.TryParseFrame(joinPacket2.AsSpan(4), out int joinMsgId2, out var joinBody2);
bool dispatchedJoin = await dispatcher.TryDispatch(testCtx, joinMsgId2, joinBody2);
Console.WriteLine($"Dispatcher 分发 BattleJoin: ok={dispatchedJoin} handled={battleHandled} sent={sentMessages.Count} (期望 True/1/2)");
if (!dispatchedJoin || battleHandled != 1 || sentMessages.Count != 2) return 1;

// 未注册消息返回 false（可回退旧逻辑）
bool notRegistered = await dispatcher.TryDispatch(testCtx, 99999, new byte[0]);
Console.WriteLine($"Dispatcher 未注册消息: ok={notRegistered} (期望 False)");
if (notRegistered) return 1;

// ===== 13. Dispatcher JSON 兼容（旧客户端 JSON 消息 → 新生成类） =====
var jsonDispatcher = new Framework.Protocol.MessageDispatcher();
int jsonLoginHandled = 0;
string? jsonAccount = null;
jsonDispatcher.RegisterSync<Framework.Protocol.Generated.Login>((ctx, msg) =>
{
    jsonLoginHandled++;
    jsonAccount = msg.Account;
}, jsonFallback: true);

// 模拟旧客户端：发送 JSON 格式的登录消息（无路由元数据，直接业务体）
byte[] jsonLoginPayload = System.Text.Encoding.UTF8.GetBytes("{\"Account\":\"bob\",\"Password\":\"secret\"}");
bool jsonDispatched = await jsonDispatcher.TryDispatch(testCtx, Framework.Protocol.Generated.Login.MsgId, jsonLoginPayload);
Console.WriteLine($"Dispatcher JSON 兼容: ok={jsonDispatched} handled={jsonLoginHandled} account={jsonAccount} (期望 True/1/bob)");
if (!jsonDispatched || jsonLoginHandled != 1 || jsonAccount != "bob") return 1;

// MemoryPack 二进制消息也走同一注册（双格式）
var jsonDispatcher2 = new Framework.Protocol.MessageDispatcher();
int binHandled = 0;
jsonDispatcher2.RegisterSync<Framework.Protocol.Generated.Login>((ctx, msg) => binHandled++, jsonFallback: true);
var binLogin = new Framework.Protocol.Generated.Login { Account = "carol", Password = "pw" };
byte[] binPacket = Framework.Protocol.ProtocolCodec.Encode(binLogin);
Framework.Protocol.ProtocolCodec.TryParseFrame(binPacket.AsSpan(4), out _, out var binBody);
bool binDispatched = await jsonDispatcher2.TryDispatch(testCtx, Framework.Protocol.Generated.Login.MsgId, binBody);
Console.WriteLine($"Dispatcher 二进制兼容: ok={binDispatched} handled={binHandled} (期望 True/1)");
if (!binDispatched || binHandled != 1) return 1;

// ===== 14. 实体备份服务（平滑分摊 + 落盘 + 恢复） =====
string backupPath = Path.Combine(Path.GetTempPath(), $"kbe_backup_{Guid.NewGuid():N}.bin");
var backupManager = new Framework.Entity.EntityManager();
var backupDef = new Framework.Entity.EntityDef { Name = "Player" }
    .Add("Hp", Framework.Entity.EntityPropertyType.Int32)
    .Add("Nickname", Framework.Entity.EntityPropertyType.String)
    .Add("Position", Framework.Entity.EntityPropertyType.Float3);
for (int i = 1; i <= 10; i++)
{
    var e = backupDef.CreateEntity(i);
    e.Set("Hp", 100 + i);
    e.Set("Nickname", $"P{i}");
    e.Set("Position", new Framework.Entity.Float3(i, 0, i * 2));
    backupManager.AddOrUpdateEntity(i, e);
}

var backupService = new Framework.Entity.EntityBackupService(backupPath, periodInTicks: 4);
backupService.AddManager(backupManager);
// 跑 4 tick 完成一轮完整备份（10 实体 / 4 tick = 每 tick ~2.5 → 分摊）
for (int t = 0; t < 4; t++)
{
    backupService.Tick();
    await Task.Delay(20); // 等待异步落盘
}
await Task.Delay(300);
long backupBytes = new FileInfo(backupPath).Length;
Console.WriteLine($"实体备份: 文件大小={backupBytes} bytes (期望 >0)");
if (backupBytes <= 0) return 1;

// 恢复验证：新建空管理器实体骨架，从备份恢复属性
var restoreManager = new Framework.Entity.EntityManager();
for (int i = 1; i <= 10; i++)
{
    restoreManager.AddOrUpdateEntity(i, backupDef.CreateEntity(i)); // 空骨架
}
var restoreService = new Framework.Entity.EntityBackupService(backupPath, periodInTicks: 4);
restoreService.AddManager(restoreManager);
int restoredCount = restoreService.RestoreFromFile();
var restoredEntity = restoreManager.GetEntity(5);
Console.WriteLine($"备份恢复: 恢复数={restoredCount} P5.Hp={restoredEntity?.Get<int>("Hp")} Nick={restoredEntity?.Get<string>("Nickname")} (期望 10/105/P5)");
if (restoredCount != 10 || restoredEntity?.Get<int>("Hp") != 105 || restoredEntity?.Get<string>("Nickname") != "P5") return 1;
File.Delete(backupPath);

// ===== 15. 实体持久化服务（对标 KBE entity_table：属性声明驱动自动存取 + 崩溃恢复） =====
string persistDir = Path.Combine(Path.GetTempPath(), $"kbe_persist_{Guid.NewGuid():N}");
var persistDef = new Framework.Entity.EntityDef { Name = "Player" }
    .Add("Hp", Framework.Entity.EntityPropertyType.Int32)
    .Add("Nickname", Framework.Entity.EntityPropertyType.String)
    .Add("Position", Framework.Entity.EntityPropertyType.Float3);
var persistFactory = new Func<long, Framework.Entity.Entity>(id => persistDef.CreateEntity(id));

// 模拟"服务器运行期"：实体被修改并自动保存
var liveManager = new Framework.Entity.EntityManager();
var persistService = new Framework.Entity.EntityPersistenceService(persistDir, persistFactory);
for (int i = 1; i <= 5; i++)
{
    var e = persistDef.CreateEntity(i);
    e.Set("Hp", 200 + i);
    e.Set("Nickname", $"S{i}");
    e.Set("Position", new Framework.Entity.Float3(i * 10, 0, i * 10));
    liveManager.AddOrUpdateEntity(i, e);
    persistService.SaveEntity(e); // 属性声明驱动自动落库（无手写 SQL/映射）
}
Console.WriteLine($"实体持久化: 已保存 5 个，目录文件数={persistService.Count("Player")} (期望 5)");
if (persistService.Count("Player") != 5) return 1;

// 模拟"服务器崩溃重启"：用 factory 重建实体骨架并从持久化自动恢复（对标 restore_entity_handler）
var recoveredManager = new Framework.Entity.EntityManager();
var recoverService = new Framework.Entity.EntityPersistenceService(persistDir, persistFactory);
var recovered = recoverService.RestoreAll("Player");
foreach (var e in recovered) recoveredManager.AddOrUpdateEntity(e.EntityId, e);
var recoveredEntity = recoveredManager.GetEntity(3);
Console.WriteLine($"崩溃恢复: 恢复数={recovered.Count} P3.Hp={recoveredEntity?.Get<int>("Hp")} Nick={recoveredEntity?.Get<string>("Nickname")} (期望 5/203/S3)");
if (recovered.Count != 5 || recoveredEntity?.Get<int>("Hp") != 203 || recoveredEntity?.Get<string>("Nickname") != "S3") return 1;

// 单实体加载 + 删除
var singleEntity = recoverService.LoadEntityById("Player", 5);
Console.WriteLine($"单实体加载: Hp={singleEntity?.Get<int>("Hp")} (期望 205)");
if (singleEntity?.Get<int>("Hp") != 205) return 1;
recoverService.DeleteEntity("Player", 5);
Console.WriteLine($"删除实体: 剩余文件数={recoverService.Count("Player")} (期望 4)");
if (recoverService.Count("Player") != 4) return 1;
Directory.Delete(persistDir, recursive: true);

// ===== 16. Center 配置化分发集成验证（MatchHandler 真实链路） =====
var centerMatchHandler = new Center.Handlers.MatchHandler();
var centerDispatcher = Center.Handlers.CenterDispatcher.BuildDispatcher(centerMatchHandler);
var centerSent = new List<(int msgId, byte[] payload)>();
var centerGatewaySession = new TestGatewaySession(centerSent);
var centerCtx = new Center.Handlers.CenterSessionContext(centerGatewaySession, 7001)
{
    RoutedUserId = 42,
    RoutedUid = "100000042",
    RoutedNickname = "Tester"
};

// 创建房间（生成类 → Dispatcher → MatchHandler → 生成类响应）
var createMsg = new Framework.Protocol.Generated.CenterCreateRoom
{
    SceneType = "PVP",
    RoomName = "TestRoom",
    MaxPlayers = 4
};
byte[] createPacket = Framework.Protocol.ProtocolCodec.Encode(createMsg);
Framework.Protocol.ProtocolCodec.TryParseFrame(createPacket.AsSpan(4), out int createMsgId, out var createBody);
bool createOk = await centerDispatcher.TryDispatch(centerCtx, createMsgId, createBody);
Console.WriteLine($"Center 创建房间: ok={createOk} (期望 True)");
if (!createOk) return 1;

// 加入房间（同一会话加入）
var centerJoinMsg = new Framework.Protocol.Generated.CenterJoinRoom { RoomId = "TestRoom" };
byte[] centerJoinPacket = Framework.Protocol.ProtocolCodec.Encode(centerJoinMsg);
Framework.Protocol.ProtocolCodec.TryParseFrame(centerJoinPacket.AsSpan(4), out int centerJoinMsgId, out var centerJoinBody);
bool joinOk = await centerDispatcher.TryDispatch(centerCtx, centerJoinMsgId, centerJoinBody);
Console.WriteLine($"Center 加入房间: ok={joinOk} (期望 True)");
if (!joinOk) return 1;

// 房间聊天
var chatMsg = new Framework.Protocol.Generated.CenterRoomChat { RoomId = "TestRoom", Content = "hello" };
byte[] chatPacket = Framework.Protocol.ProtocolCodec.Encode(chatMsg);
Framework.Protocol.ProtocolCodec.TryParseFrame(chatPacket.AsSpan(4), out int chatMsgId, out var chatBody);
bool chatOk = await centerDispatcher.TryDispatch(centerCtx, chatMsgId, chatBody);
Console.WriteLine($"Center 房间聊天: ok={chatOk} (期望 True)");
if (!chatOk) return 1;

// 离开房间
var leaveMsg = new Framework.Protocol.Generated.CenterLeaveRoom { RoomId = "TestRoom" };
byte[] leavePacket = Framework.Protocol.ProtocolCodec.Encode(leaveMsg);
Framework.Protocol.ProtocolCodec.TryParseFrame(leavePacket.AsSpan(4), out int leaveMsgId, out var leaveBody);
bool leaveOk = await centerDispatcher.TryDispatch(centerCtx, leaveMsgId, leaveBody);
Console.WriteLine($"Center 离开房间: ok={leaveOk} 发送包数={centerSent.Count} (期望 True/>=4)");
if (!leaveOk || centerSent.Count < 4) return 1;

// ===== 17. Leader 选举验证（主备高可用：争锁 + 故障接管） =====
string leaderLock = Path.Combine(Path.GetTempPath(), $"leader_test_{Guid.NewGuid():N}.lock");
var leaderA = new Framework.Core.LeaderElection(leaderLock, "Center-A", heartbeatIntervalMs: 300);
var leaderB = new Framework.Core.LeaderElection(leaderLock, "Center-B", heartbeatIntervalMs: 300);
Console.WriteLine($"Leader 选举: A={leaderA.IsLeader} B={leaderB.IsLeader} (期望 True/False，同一时刻仅一个 Leader)");
if (!leaderA.IsLeader || leaderB.IsLeader) return 1;

// 模拟 A 故障（释放锁）→ B 应自动接管
leaderA.Dispose();
await Task.Delay(1000);
Console.WriteLine($"Leader 故障接管: A={leaderA.IsLeader} B={leaderB.IsLeader} (期望 False/True)");
if (!leaderB.IsLeader) return 1;

// B 主动让出 → 重新创建 A 可再抢
leaderB.StepDown();
leaderA = new Framework.Core.LeaderElection(leaderLock, "Center-A", heartbeatIntervalMs: 300);
await Task.Delay(500);
Console.WriteLine($"Leader 重新选举: A={leaderA.IsLeader} B={leaderB.IsLeader} (期望 True/False)");
if (!leaderA.IsLeader || leaderB.IsLeader) return 1;

leaderA.Dispose();
leaderB.Dispose();
File.Delete(leaderLock);

// ===== 18. DB 配置化分发验证（DbDispatcher 全量注册 + 双格式 + RequestId 路由） =====

// 18.1 全量注册：DB 服务器 20 条请求消息全部迁移到强类型分发
var dbDispatcher = DB.Handlers.DbDispatcher.BuildDispatcher();
Console.WriteLine($"DB Dispatcher 注册消息数: {dbDispatcher.RegisteredCount} (期望 20)");
if (dbDispatcher.RegisteredCount != 20) return 1;

// 18.2 生成类字段对齐 round-trip（defs 与旧协议对齐：FriendUniqueId/TargetUniqueId/UserId）
var dbAddFriend = new Framework.Protocol.Generated.DbFriendAdd { UserId = 7, FriendUniqueId = "100000008", Remark = "hi" };
byte[] dbAddFriendBody = MemoryPack.MemoryPackSerializer.Serialize(dbAddFriend);
var dbAddFriendBack = MemoryPack.MemoryPackSerializer.Deserialize<Framework.Protocol.Generated.DbFriendAdd>(dbAddFriendBody);
Console.WriteLine($"DB 生成类 round-trip: UserId={dbAddFriendBack?.UserId} FriendUniqueId={dbAddFriendBack?.FriendUniqueId} Remark={dbAddFriendBack?.Remark} (期望 7/100000008/hi)");
if (dbAddFriendBack?.UserId != 7 || dbAddFriendBack?.FriendUniqueId != "100000008" || dbAddFriendBack?.Remark != "hi") return 1;

var dbChangePwd = new Framework.Protocol.Generated.DbChangePassword { UserId = 3, Account = "alice", OldPassword = "old", NewPassword = "new" };
byte[] dbChangePwdBody = MemoryPack.MemoryPackSerializer.Serialize(dbChangePwd);
var dbChangePwdBack = MemoryPack.MemoryPackSerializer.Deserialize<Framework.Protocol.Generated.DbChangePassword>(dbChangePwdBody);
Console.WriteLine($"DB 改密 round-trip: UserId={dbChangePwdBack?.UserId} Account={dbChangePwdBack?.Account} (期望 3/alice)");
if (dbChangePwdBack?.UserId != 3 || dbChangePwdBack?.Account != "alice") return 1;

// 18.3 JSON 旧格式兼容：旧客户端（Login/Game）发送的 JSON 字段名可直接映射到生成类（双格式兼容基础）
var dbBlacklistJson = "{\"UserId\":9,\"TargetUniqueId\":\"100000010\"}";
var dbBlacklistBack = Newtonsoft.Json.JsonConvert.DeserializeObject<Framework.Protocol.Generated.DbBlacklistAdd>(dbBlacklistJson);
Console.WriteLine($"DB JSON 兼容: UserId={dbBlacklistBack?.UserId} TargetUniqueId={dbBlacklistBack?.TargetUniqueId} (期望 9/100000010)");
if (dbBlacklistBack?.UserId != 9 || dbBlacklistBack?.TargetUniqueId != "100000010") return 1;

var dbApplyJson = "{\"RequesterUserId\":11,\"TargetUniqueId\":\"100000012\",\"Message\":\"加个好友\"}";
var dbApplyBack = Newtonsoft.Json.JsonConvert.DeserializeObject<Framework.Protocol.Generated.DbFriendApplyCreate>(dbApplyJson);
Console.WriteLine($"DB 申请 JSON 兼容: Requester={dbApplyBack?.RequesterUserId} Target={dbApplyBack?.TargetUniqueId} Msg={dbApplyBack?.Message} (期望 11/100000012/加个好友)");
if (dbApplyBack?.RequesterUserId != 11 || dbApplyBack?.TargetUniqueId != "100000012" || dbApplyBack?.Message != "加个好友") return 1;

// 18.4 DbSessionContext + RequestId 路由：分发处理器经 DbSessionContext 发响应时自动附加请求 ID（等价旧 RequestContextSession）
var dbSentFrames = new List<(int msgId, byte[] payload)>();
var dbGatewaySession = new TestGatewaySession(dbSentFrames);
var dbCtx = new DB.Handlers.DbSessionContext(dbGatewaySession, 4242);
var dbRouteDispatcher = new Framework.Protocol.MessageDispatcher();
int dbRouteHandled = 0;
dbRouteDispatcher.RegisterSync<Framework.Protocol.Generated.DbResolveUserByUniqueId>((ctx, msg) =>
{
    dbRouteHandled++;
    ctx.Send(new Framework.Protocol.Generated.DbResolveUserByUniqueIdResult
    {
        Success = true,
        Message = $"resolved {msg.UniqueId}",
        UserId = 5,
        Nickname = "npc5"
    });
}, jsonFallback: true);

var dbResolveMsg = new Framework.Protocol.Generated.DbResolveUserByUniqueId { UniqueId = "100000005" };
byte[] dbResolvePacket = Framework.Protocol.ProtocolCodec.Encode(dbResolveMsg);
Framework.Protocol.ProtocolCodec.TryParseFrame(dbResolvePacket.AsSpan(4), out int dbResolveMsgId, out var dbResolveBody);
bool dbResolveOk = await dbRouteDispatcher.TryDispatch(dbCtx, dbResolveMsgId, dbResolveBody);
bool dbRequestIdOk = false;
long dbExtractedRequestId = 0;
string? dbResolvedNickname = null;
if (dbRouteHandled == 1 && dbSentFrames.Count > 0)
{
    // 帧格式：[TotalLength][MsgId][AttachRequestId(ResultBody)]
    byte[] dbFrame = dbSentFrames[0].payload;
    var dbPayloadWithMetadata = dbFrame.AsMemory(8);
    if (Shared.RouteMetadata.TryExtractRequestId(dbPayloadWithMetadata, out dbExtractedRequestId, out byte[] dbCleanPayload))
    {
        var dbResultBack = MemoryPack.MemoryPackSerializer.Deserialize<Framework.Protocol.Generated.DbResolveUserByUniqueIdResult>(dbCleanPayload);
        dbResolvedNickname = dbResultBack?.Nickname;
        dbRequestIdOk = dbExtractedRequestId == 4242 && dbResolvedNickname == "npc5";
    }
}
Console.WriteLine($"DB RequestId 路由: ok={dbResolveOk} handled={dbRouteHandled} requestId={dbExtractedRequestId} nickname={dbResolvedNickname} (期望 True/1/4242/npc5)");
if (!dbResolveOk || !dbRequestIdOk) return 1;

Console.WriteLine("\n===== 全部验证通过 =====");
return 0;

// 测试用会话上下文
sealed class TestSessionContext : Framework.Protocol.ISessionContext
{
    private readonly List<(int, byte[])> sent;
    public long ClientSessionId => 100;
    public TestSessionContext(List<(int, byte[])> sent) { this.sent = sent; }

    public void Send(int msgId, ReadOnlyMemory<byte> payload)
    {
        sent.Add((msgId, payload.ToArray()));
        Console.WriteLine($"[dispatcher] Send MsgId={msgId} Len={payload.Length}");
    }

    public void Send(Framework.Protocol.IGameMessage message)
    {
        byte[] packet = Framework.Protocol.ProtocolCodec.Encode(message);
        Framework.Protocol.ProtocolCodec.TryParseFrame(packet.AsSpan(4), out int msgId, out var body);
        sent.Add((msgId, body.ToArray()));
        Console.WriteLine($"[dispatcher] SendMsg Type={message.GetType().Name} MsgId={msgId}");
    }

    public void SendTo(long clientSessionId, int msgId, ReadOnlyMemory<byte> payload) => Send(msgId, payload);
}

/// <summary>测试用网关会话（实现 Network.ISession，记录发送数据）。</summary>
sealed class TestGatewaySession : Network.ISession
{
    private readonly List<(int, byte[])> sent;
    public long SessionId { get; } = 90001;
    public System.Net.EndPoint? RemoteEndPoint { get; } = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0);
    public bool IsConnected => true;
    public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;
    public object? UserData { get; set; }

    public TestGatewaySession(List<(int, byte[])> sent) { this.sent = sent; }

    public void Send(ReadOnlyMemory<byte> data)
    {
        sent.Add((0, data.ToArray())); // 记录原始帧（含长度头，此处仅计数用）
        Console.WriteLine($"[gateway] Send {data.Length} bytes");
    }

    public void Close() { }
}

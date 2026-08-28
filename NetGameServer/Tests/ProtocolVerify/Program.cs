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

// ===== 11b. EntityCall 回执/超时（D3：callId + 超时表 + 回执关联） =====
// 模拟跨进程异步调用：发送方 EntityCall.Remote → 截获消息 → 接收方 ExecuteRemoteCall → 回执经 EntityCallHub 关联完成
var receivedCalls = new System.Collections.Generic.List<Framework.Protocol.Generated.EntityRemoteCall>();
var asyncCall = Framework.Entity.EntityCall.Remote("Battle-test", 9001, call => receivedCalls.Add(call));

int ackCount = 0;
object? ackValue = null;
long asyncCallId = asyncCall.CallAsync("AddScore", new object?[] { 10 }, (success, value) =>
{
    ackCount++;
    ackValue = value;
}, timeoutMs: 5000);
Console.WriteLine($"EntityCall 异步调用: callId={asyncCallId} pending={Framework.Entity.EntityCallHub.PendingCount} sent={receivedCalls.Count} (期望 >0/1/1)");
if (asyncCallId <= 0 || receivedCalls.Count != 1 || Framework.Entity.EntityCallHub.PendingCount != 1) return 1;

// 模拟跨进程送达并执行，构造携带同一 CallId 的回执
var deliveredCall = receivedCalls[0];
var callResult = callManager.ExecuteRemoteCall(deliveredCall);
Console.WriteLine($"EntityCall 执行回执: callId={callResult!.CallId} success={callResult.Success} (期望 {asyncCallId}/True)");
if (callResult == null || callResult.CallId != asyncCallId || !callResult.Success) return 1;

// 回执关联完成回调
bool ackConsumed = Framework.Entity.EntityCallHub.HandleResult(callResult);
Console.WriteLine($"EntityCall 回执关联: consumed={ackConsumed} ackCount={ackCount} value={ackValue} Hp={callEntity.Get<int>("Hp")} (期望 True/1/65/65)");
if (!ackConsumed || ackCount != 1 || (int?)ackValue != 65 || callEntity.Get<int>("Hp") != 65) return 1;

// 超时：注册一个永不回执的调用 → SweepExpired 判定失败（Success=false）
int timeoutCount = 0;
bool timeoutSuccess = true;
long timeoutCallId = asyncCall.CallAsync("AddScore", new object?[] { 100 }, (success, value) =>
{
    timeoutCount++;
    timeoutSuccess = success;
}, timeoutMs: 50);
int expired = Framework.Entity.EntityCallHub.SweepExpired(DateTime.UtcNow.AddMilliseconds(200));
Console.WriteLine($"EntityCall 超时: callId={timeoutCallId} expired={expired} timeoutCount={timeoutCount} timeoutSuccess={timeoutSuccess} pending={Framework.Entity.EntityCallHub.PendingCount} (期望 >0/1/1/False/0)");
if (timeoutCallId <= 0 || expired != 1 || timeoutCount != 1 || timeoutSuccess || Framework.Entity.EntityCallHub.PendingCount != 0) return 1;

// fire-and-forget（CallId=0）：不注册待回执、接收方无需回执
var fireAndForget = Framework.Entity.EntityCall.Remote("Battle-test", 9001, call => receivedCalls.Add(call));
int beforeFaf = Framework.Entity.EntityCallHub.PendingCount;
fireAndForget.Call("AddScore", 5);
Console.WriteLine($"EntityCall fire-and-forget: sent={receivedCalls.Count} lastCallId={receivedCalls[^1].CallId} pending={Framework.Entity.EntityCallHub.PendingCount} (期望 3/0/{beforeFaf})");
if (receivedCalls.Count != 3 || receivedCalls[^1].CallId != 0 || Framework.Entity.EntityCallHub.PendingCount != beforeFaf) return 1;
// fire-and-forget 不回执：ExecuteRemoteCall 返回 null
var fafResult = callManager.ExecuteRemoteCall(receivedCalls[^1]);
if (fafResult != null) return 1;

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

// ===== 15.5 实体在线迁移：属性全量序列化（含 CELL_PRIVATE 内部状态）→ 恢复回环 =====
{
    var migDef = new Framework.Entity.EntityDef { Name = "Player" }
        .Add("Hp", Framework.Entity.EntityPropertyType.Int32)
        .Add("Nickname", Framework.Entity.EntityPropertyType.String)
        .Add("Position", Framework.Entity.EntityPropertyType.Float3)
        .Add("Equipment", Framework.Entity.EntityPropertyType.Int32List, syncToClient: false); // 内部状态也要迁移
    var migSrc = migDef.CreateEntity(777);
    migSrc.Set("Hp", 88);
    migSrc.Set("Nickname", "Migrant");
    migSrc.Set("Position", new Framework.Entity.Float3(1, 2, 3));
    migSrc.Set("Equipment", new List<int> { 7, 8, 9 });

    byte[] migProps = Framework.Entity.PropertyCodec.SerializeAllValues(migSrc.CopyValues(), migDef, onlySyncToClient: false);
    var migDst = migDef.CreateEntity(777);
    Framework.Entity.PropertyCodec.DeserializeInto(migDst, migProps, applyDirty: false);
    bool migOk = migDst.Get<int>("Hp") == 88
        && migDst.Get<string>("Nickname") == "Migrant"
        && migDst.Get<Framework.Entity.Float3>("Position").Equals(new Framework.Entity.Float3(1, 2, 3))
        && migDst.Get<List<int>>("Equipment").SequenceEqual(new[] { 7, 8, 9 });
    Console.WriteLine($"实体迁移: Props={migProps.Length}B 恢复 Hp={migDst.Get<int>("Hp")} Nick={migDst.Get<string>("Nickname")} Equip={string.Join(",", migDst.Get<List<int>>("Equipment"))} (期望 88/Migrant/7,8,9)");
    if (!migOk) return 1;
}

// ===== 15.6 玩法实体迁移 v2（D4）：属主玩法实体同包随迁 + 属主绑定恢复 =====
{
    // 源节点：玩家属主的玩法实体骨架（含公开/OWN_CLIENT/CELL_PRIVATE 三种作用域，模拟 Skill/Item 形态）
    var skillDef = new Framework.Entity.EntityDef { Name = "Skill" }
        .Add("Level", Framework.Entity.EntityPropertyType.Int32)
        .Add("CooldownRemaining", Framework.Entity.EntityPropertyType.Int32, syncToClient: true, scope: Framework.Entity.EntitySyncScope.OwnClient)
        .Add("Casts", Framework.Entity.EntityPropertyType.Int32, syncToClient: false);
    var itemDef = new Framework.Entity.EntityDef { Name = "Item" }
        .Add("ItemId", Framework.Entity.EntityPropertyType.Int32, syncToClient: true, scope: Framework.Entity.EntitySyncScope.OwnClient)
        .Add("Count", Framework.Entity.EntityPropertyType.Int32, syncToClient: true, scope: Framework.Entity.EntitySyncScope.OwnClient);

    var skill = skillDef.CreateEntity(9001);
    skill.OwnerClientId = 777;
    skill.Set("Level", 5);
    skill.Set("CooldownRemaining", 3);
    skill.Set("Casts", 12);

    var item = itemDef.CreateEntity(9002);
    item.OwnerClientId = 777;
    item.Set("ItemId", 6001);
    item.Set("Count", 4);

    var migReq = new Framework.Protocol.Generated.EntityMigrateRequest
    {
        SourceNodeId = "Battle-A",
        TargetNodeId = "Battle-B",
        ClientSessionId = 777,
        EntityId = 777,
        EntityType = "Player",
        SceneId = "S1",
        Props = new byte[] { 1, 2, 3 },
        OwnedEntities = new List<Framework.Protocol.Generated.EntityMigratePayload>
        {
            new Framework.Protocol.Generated.EntityMigratePayload
            {
                EntityId = skill.EntityId,
                EntityType = skill.TypeName,
                Props = Framework.Entity.PropertyCodec.SerializeAllValues(skill.CopyValues(), skill.Def, onlySyncToClient: false)
            },
            new Framework.Protocol.Generated.EntityMigratePayload
            {
                EntityId = item.EntityId,
                EntityType = item.TypeName,
                Props = Framework.Entity.PropertyCodec.SerializeAllValues(item.CopyValues(), item.Def, onlySyncToClient: false)
            }
        }
    };

    // 同包 round-trip（MemoryPack，含随迁属主实体列表）
    byte[] migReqBytes = MemoryPack.MemoryPackSerializer.Serialize(migReq);
    var migReqBack = MemoryPack.MemoryPackSerializer.Deserialize<Framework.Protocol.Generated.EntityMigrateRequest>(migReqBytes);
    bool reqOk = migReqBack?.OwnedEntities?.Count == 2
        && migReqBack.OwnedEntities[0].EntityId == 9001
        && migReqBack.OwnedEntities[1].EntityType == "Item";
    Console.WriteLine($"迁移随迁同包 round-trip: Count={migReqBack?.OwnedEntities?.Count} EntityId0={migReqBack?.OwnedEntities?[0].EntityId} Type1={migReqBack?.OwnedEntities?[1].EntityType} (期望 2/9001/Item)");
    if (!reqOk) return 1;

    // 属主绑定恢复：按 RestoreMigratedEntity 语义解包 + OwnerClientId 绑定
    var skillDst = skillDef.CreateEntity(migReqBack!.OwnedEntities![0].EntityId);
    Framework.Entity.PropertyCodec.DeserializeInto(skillDst, migReqBack.OwnedEntities[0].Props, applyDirty: false);
    skillDst.OwnerClientId = migReqBack.ClientSessionId;
    var itemDst = itemDef.CreateEntity(migReqBack.OwnedEntities[1].EntityId);
    Framework.Entity.PropertyCodec.DeserializeInto(itemDst, migReqBack.OwnedEntities[1].Props, applyDirty: false);
    itemDst.OwnerClientId = migReqBack.ClientSessionId;

    bool restoreOk = skillDst.Get<int>("Level") == 5
        && skillDst.Get<int>("CooldownRemaining") == 3
        && skillDst.Get<int>("Casts") == 12
        && skillDst.OwnerClientId == 777
        && itemDst.Get<int>("ItemId") == 6001
        && itemDst.Get<int>("Count") == 4
        && itemDst.OwnerClientId == 777;
    Console.WriteLine($"随迁玩法实体恢复: Skill Level={skillDst.Get<int>("Level")} Cooldown={skillDst.Get<int>("CooldownRemaining")} Casts={skillDst.Get<int>("Casts")} Owner={skillDst.OwnerClientId} / Item Id={itemDst.Get<int>("ItemId")} Count={itemDst.Get<int>("Count")} Owner={itemDst.OwnerClientId} (期望 5/3/12/777 / 6001/4/777)");
    if (!restoreOk) return 1;
}

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

// ===== 17b. Center 平滑加权负载均衡验证（SWRR + 过期负载惩罚） =====
var lbManager = Center.Handlers.NodeManager.Instance;
var lbSent = new List<(int, byte[])>();
var lbSessionA = new TestGatewaySession(lbSent);
var lbSessionB = new TestGatewaySession(lbSent);
lbManager.RegisterNode("Battle-A", "Battle", "127.0.0.1", 31310, lbSessionA);
lbManager.RegisterNode("Battle-B", "Battle", "127.0.0.1", 31311, lbSessionB);
lbManager.UpdateLoad("Battle-A", 80);
lbManager.UpdateLoad("Battle-B", 10);

// 平滑加权：A 负载高(80)、B 负载低(10)，50 次选择应偏向 B（权重 20:90，约 82% B）且两者都有命中
int lbPickA = 0, lbPickB = 0;
for (int i = 0; i < 50; i++)
{
    var pick = lbManager.GetBestBattleNode();
    if (pick == "Battle-A") lbPickA++;
    else if (pick == "Battle-B") lbPickB++;
}
Console.WriteLine($"平滑加权选择: A={lbPickA} B={lbPickB} (期望 偏向低负载 B：B>40 且 A>0)");
if (lbPickA == 0 || lbPickB <= 40) return 1;

// 过期负载惩罚：把 A 心跳改旧（>30s 阈值）→ 仅 B 被选中
if (lbManager.GetNode("Battle-A") is { } nodeA)
{
    nodeA.LastHeartbeat = DateTime.UtcNow.AddSeconds(-60);
}
int stalePickA = 0, stalePickB = 0;
for (int i = 0; i < 10; i++)
{
    var pick = lbManager.GetBestBattleNode();
    if (pick == "Battle-A") stalePickA++;
    else if (pick == "Battle-B") stalePickB++;
}
Console.WriteLine($"过期负载惩罚: A={stalePickA} B={stalePickB} (期望 0/10，A 心跳过期被剔除)");
if (stalePickA != 0 || stalePickB != 10) return 1;

// 等负载公平：恢复 A 心跳、AB 同负载 → 不再被单一节点垄断（大致各半）
lbManager.GetNode("Battle-A")!.LastHeartbeat = DateTime.UtcNow;
lbManager.UpdateLoad("Battle-A", 50);
lbManager.UpdateLoad("Battle-B", 50);
int fairA = 0, fairB = 0;
for (int i = 0; i < 40; i++)
{
    var pick = lbManager.GetBestBattleNode();
    if (pick == "Battle-A") fairA++;
    else if (pick == "Battle-B") fairB++;
}
Console.WriteLine($"等负载公平: A={fairA} B={fairB} (期望 大致 20/20，无单点垄断)");
if (fairA == 0 || fairB == 0 || Math.Abs(fairA - fairB) > 10) return 1;

// 清理测试节点，避免污染后续验证
lbManager.RemoveNodeBySession(lbSessionA);
lbManager.RemoveNodeBySession(lbSessionB);

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

// ===== 19. 并发注入压测（验证 P0 并发修复：OrderedTaskQueue 严格保序 + MessageDispatcher 免锁读） =====
{
    // 19a. OrderedTaskQueue：16 生产者并发投递 960 任务 / 6 key。
    // 并发下各生产者投递顺序无法对外部编号对齐，因此断言「单生产者 → 单 key」的 FIFO：
    // 队列必须保持每个生产者自己投递到同一 key 的任务的相对顺序。
    var bigQueue = new Framework.Core.OrderedTaskQueue("stress");
    var bigLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
    const int producers = 16;
    const int perProducer = 60;
    const int stressKeys = 6;
    var allTasks = new List<Task>();
    var tasksLock = new object();
    var injectors = new List<Task>();
    for (int p = 0; p < producers; p++)
    {
        int pid = p;
        injectors.Add(Task.Run(() =>
        {
            var local = new List<Task>();
            for (int s = 0; s < perProducer; s++)
            {
                int key = (s + pid) % stressKeys;
                int seq = s;
                local.Add(bigQueue.Enqueue($"p{pid}:k{key}", () => bigLog.Enqueue($"{pid}:{key}:{seq}")));
            }
            lock (tasksLock) allTasks.AddRange(local);
        }));
    }
    await Task.WhenAll(injectors);
    await Task.WhenAll(allTasks);

    bool bigOrdered = true;
    for (int pid = 0; pid < producers && bigOrdered; pid++)
    {
        for (int key = 0; key < stressKeys; key++)
        {
            int last = -1;
            foreach (var entry in bigLog.Where(e => e.StartsWith($"{pid}:{key}:")))
            {
                int seq = int.Parse(entry.Split(':')[2]);
                if (seq <= last) { bigOrdered = false; break; }
                last = seq;
            }
        }
    }
    int stressTotal = producers * perProducer;
    Console.WriteLine($"并发注入 OrderedTaskQueue: 任务数={bigLog.Count} 单生产者逐key保序={bigOrdered} (期望 {stressTotal}/True)");
    if (bigLog.Count != stressTotal || !bigOrdered) return 1;

    // 19b. MessageDispatcher：8 线程 × 400 次并发分发，免锁读路由正确 + 计数准确
    var conDsp = new Framework.Protocol.MessageDispatcher();
    var conCounters = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
    conDsp.RegisterSync<Framework.Protocol.Generated.Login>((ctx, msg) => conCounters.AddOrUpdate(1, 1, (_, c) => c + 1));
    conDsp.RegisterSync<Framework.Protocol.Generated.ResetPassword>((ctx, msg) => conCounters.AddOrUpdate(2, 1, (_, c) => c + 1));
    conDsp.RegisterSync<Framework.Protocol.Generated.UpdateNickname>((ctx, msg) => conCounters.AddOrUpdate(3, 1, (_, c) => c + 1));
    conDsp.RegisterSync<Framework.Protocol.Generated.Logout>((ctx, msg) => conCounters.AddOrUpdate(4, 1, (_, c) => c + 1));

    byte[] EncodeBody(Framework.Protocol.IGameMessage m)
    {
        byte[] frame = Framework.Protocol.ProtocolCodec.Encode(m);
        Framework.Protocol.ProtocolCodec.TryParseFrame(frame.AsSpan(4), out _, out var body);
        return body.ToArray();
    }
    byte[] conBody1 = EncodeBody(new Framework.Protocol.Generated.Login { Account = "a", Password = "p" });
    byte[] conBody2 = EncodeBody(new Framework.Protocol.Generated.ResetPassword { Account = "a", OldPassword = "o", NewPassword = "n" });
    byte[] conBody3 = EncodeBody(new Framework.Protocol.Generated.UpdateNickname { UserId = 1, NewNickname = "x" });
    byte[] conBody4 = EncodeBody(new Framework.Protocol.Generated.Logout { UserId = 1 });
    int[] conIds = new[]
    {
        Framework.Protocol.Generated.Login.MsgId,
        Framework.Protocol.Generated.ResetPassword.MsgId,
        Framework.Protocol.Generated.UpdateNickname.MsgId,
        Framework.Protocol.Generated.Logout.MsgId
    };
    byte[][] conBodies = { conBody1, conBody2, conBody3, conBody4 };

    var conCtx = new TestSessionContext(new List<(int, byte[])>());
    var conTasks = new List<Task>();
    const int conPerThread = 400;
    for (int t = 0; t < 8; t++)
    {
        conTasks.Add(Task.Run(async () =>
        {
            for (int i = 0; i < conPerThread; i++)
            {
                int pick = (i + t) % 4;
                await conDsp.TryDispatch(conCtx, conIds[pick], conBodies[pick]);
            }
        }));
    }
    await Task.WhenAll(conTasks);

    int conTotal = 0;
    foreach (var kv in conCounters) conTotal += kv.Value;
    bool conOk = conTotal == 8 * conPerThread && conCounters.Count == 4 && conDsp.RegisteredCount == 4;
    Console.WriteLine($"并发注入 MessageDispatcher: 分发={conTotal}/{8 * conPerThread} 注册={conDsp.RegisteredCount} 免锁读稳定={conOk} (期望 {8 * conPerThread}/4/True)");
    if (!conOk) return 1;
}

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

using System.Collections.Concurrent;
using System.Net.Sockets;
using Framework.Protocol.Generated;
using MemoryPack;
using Network.Routing;
using Network.Tcp;
using Shared;
using GenIds = Framework.Protocol.Generated.MessageIds;

// ===== NetworkVerify：传输层 + Battle 进程内集成验证 =====
// 1. 传输层：TcpServer 回显 + 4 客户端并发发送 —— 验证写队列下单包原子性与按客户端顺序
// 2. Battle 集成：进程内启动 BattleServerApp，走完整协议链路：
//    认证握手 → 加入房间 → 全量快照（NPC/任务/技能/物品）→ EntitySync 自身增量回发（owner 可见）
//    → ScriptAction 伤害（玩家掉血 / NPC 击杀）→ NPC 巡逻 Witness 自动广播

string baseDir = AppContext.BaseDirectory;
Directory.SetCurrentDirectory(baseDir);

// 测试配置（端口避开默认 31300-31307；Center 指向不可达端口，注册重试不影响测试）
File.WriteAllText(Path.Combine(baseDir, "appsettings.json"), """
{
  "BattlePort": 31327,
  "BattleHost": "127.0.0.1",
  "CenterHost": "127.0.0.1",
  "CenterPort": 31999,
  "CenterNodeSharedSecret": "change-this-secret",
  "ReconnectGraceSeconds": 2,
  "GatewayPort": 31400,
  "GatewayHost": "127.0.0.1",
  "LoginPort": 31410,
  "GamePort": 31411,
  "BattleNodes": "[\"127.0.0.1:31420\",\"127.0.0.1:31421\"]"
}
""");

Shared.Log.Configure(false, Path.Combine(baseDir, "logs", "NetworkVerify.log"), "Warning");

int failures = 0;
void Check(bool ok, string name, string detail = "")
{
    Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {name}{(detail.Length > 0 ? $" -> {detail}" : "")}");
    if (!ok) failures++;
}

static async Task<bool> WaitUntil(Func<bool> cond, TimeSpan timeout)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (!cond())
    {
        if (sw.Elapsed > timeout) return false;
        await Task.Delay(50);
    }
    return true;
}

// ========== 第一部分：传输层并发回显压测 ==========
Console.WriteLine("== 传输层：并发发送原子性/顺序（写队列） ==");
{
    var echoServer = new TcpServer();
    echoServer.OnDataReceived += (session, data) =>
    {
        // 只回弹 payload（不含 msgid），保证回显包 = [len][msgid(0)][clientId(4)][seq(4)]
        if (data.Length >= 12)
        {
            byte[] payload = data.Slice(4).ToArray();
            byte[] packet = PacketBuilder.BuildPacket(0, payload, out int len);
            Network.PacketSender.Send(session, packet, len);
        }
    };
    await echoServer.StartAsync(31337);

    const int Clients = 4, PacketsPerClient = 200;
    var tasks = new List<Task>();
    for (int c = 0; c < Clients; c++)
    {
        int clientId = c;
        tasks.Add(Task.Run(async () =>
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", 31337);
            var stream = tcp.GetStream();
            var reader = new LengthPrefixedPacketReader();
            var buffer = new byte[8192];
            var received = new List<int>();

            var sendTask = Task.Run(() =>
            {
                for (int s = 0; s < PacketsPerClient; s++)
                {
                    byte[] payload = new byte[8];
                    BitConverter.GetBytes(clientId).CopyTo(payload, 0);
                    BitConverter.GetBytes(s).CopyTo(payload, 4);
                    byte[] packet = PacketBuilder.BuildPacket(0, payload, out int len);
                    stream.Write(packet.AsSpan(0, len));
                    System.Buffers.ArrayPool<byte>.Shared.Return(packet);
                }
            });

            while (received.Count < PacketsPerClient)
            {
                int n = await stream.ReadAsync(buffer);
                if (n == 0) break;
                reader.Append(buffer.AsSpan(0, n));
                while (reader.TryReadPacket(out var pkt))
                {
                    // 回显包（去长度前缀后）：[msgId(4)][clientId(4)][seq(4)]，msgId 恒为 0
                    if (pkt.Length < 12) continue;
                    int c2 = BitConverter.ToInt32(pkt.Span.Slice(4, 4));
                    int s2 = BitConverter.ToInt32(pkt.Span.Slice(8, 4));
                    if (c2 == clientId) received.Add(s2);
                }
            }
            await sendTask;

            bool ordered = true;
            for (int i = 1; i < received.Count; i++)
            {
                if (received[i] <= received[i - 1]) { ordered = false; break; }
            }
            Check(ordered && received.Count == PacketsPerClient,
                $"客户端 {clientId}", $"收到 {received.Count}/{PacketsPerClient} 顺序保持={ordered}");
        }));
    }
    await Task.WhenAll(tasks);
    await echoServer.StopAsync();
}

// ========== 第二部分：Battle 进程内集成 ==========
Console.WriteLine("== Battle 集成：完整协议链路（进程内） ==");
long clientSessionId = 9001;
try
{
    using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await Battle.BattleServerApp.StartNetworkAsync().WaitAsync(startCts.Token);
    Check(true, "Battle 启动");
}
catch (Exception ex)
{
    Check(false, "Battle 启动", ex.Message);
    return 1;
}

using (var client = new TcpClient())
{
    await client.ConnectAsync("127.0.0.1", 31327);
    var stream = client.GetStream();
    var reader = new LengthPrefixedPacketReader();
    var buffer = new byte[8192];

    var joinResults = new ConcurrentQueue<BattleJoinResult>();
    var snapshots = new ConcurrentDictionary<long, EntitySnapshot>();
    var deltas = new ConcurrentQueue<EntityDeltaSync>();

    var readTask = Task.Run(async () =>
    {
        while (true)
        {
            int n = await stream.ReadAsync(buffer);
            if (n == 0) break;
            reader.Append(buffer.AsSpan(0, n));
            while (reader.TryReadPacket(out var pkt))
            {
                if (pkt.Length < 4) continue;
                int msgId = BitConverter.ToInt32(pkt.Span.Slice(0, 4));
                var payload = pkt.Slice(4);
                switch (msgId)
                {
                    case GenIds.BattleJoinResult:
                        var res = MemoryPackSerializer.Deserialize<BattleJoinResult>(payload.Span);
                        if (res != null) joinResults.Enqueue(res);
                        break;
                    case GenIds.EntitySnapshot:
                        var snap = MemoryPackSerializer.Deserialize<EntitySnapshot>(payload.Span);
                        if (snap != null) snapshots[snap.EntityId] = snap;
                        break;
                    case GenIds.EntityDeltaSync:
                        var d = MemoryPackSerializer.Deserialize<EntityDeltaSync>(payload.Span);
                        if (d != null) deltas.Enqueue(d);
                        break;
                }
            }
        }
    });

    void Send(int msgId, byte[] payload, long targetSessionId)
    {
        byte[] routed = Shared.RouteMetadata.AttachClientSessionId(payload, targetSessionId);
        byte[] packet = PacketBuilder.BuildPacket(msgId, routed, out int len);
        stream.Write(packet.AsSpan(0, len));
        System.Buffers.ArrayPool<byte>.Shared.Return(packet);
    }

    // 1. 内部认证握手
    var authFilter = new Framework.Core.Security.InternalAuthFilter("change-this-secret", "TestGateway-127.0.0.1:1");
    byte[] authPacket = authFilter.BuildAuthPacket();
    byte[] authFramed = PacketBuilder.BuildPacket(Framework.Core.Security.InternalAuthFilter.AuthMsgId, authPacket.AsSpan(4), out int authLen);
    stream.Write(authFramed.AsSpan(0, authLen));
    System.Buffers.ArrayPool<byte>.Shared.Return(authFramed);

    // 2. 加入房间（PVP：无 AOI，全量快照）
    var join = new BattleJoin { RoomId = "PVP", SceneName = "集成测试", SceneType = "PVP", MaxPlayers = 10 };
    Send(GenIds.BattleJoin, join.Serialize(), clientSessionId);

    // 3. 等待加入结果 + 全量快照（玩家 + 3 NPC + 任务 + 技能 + 物品）
    bool joined = await WaitUntil(() => joinResults.Count > 0 && snapshots.Count >= 5, TimeSpan.FromSeconds(6));
    var joinRes = joinResults.FirstOrDefault();
    Check(joined && joinRes?.Success == true, "加入房间成功", joinRes?.Message ?? "无结果");

    // 从快照识别 NPC（Hp=50）：解析快照属性
    long npcId = 0;
    foreach (var snap in snapshots.Values)
    {
        if (snap.EntityId < (1L << 40)) continue; // 玩法实体 ID 高位基址
        var skel = Battle.Entities.GameplayEntityDefs.Npc.CreateEntity(snap.EntityId);
        Framework.Entity.PropertyCodec.DeserializeInto(skel, snap.Props, applyDirty: false);
        if (skel.Get<int>("Hp") == 50)
        {
            npcId = snap.EntityId;
            break;
        }
    }
    Check(npcId != 0, "识别 NPC 快照（Hp=50）", $"npcId={npcId} 快照数={snapshots.Count}");

    // 4. NPC 巡逻 Witness：无需客户端消息，位置增量自动广播
    int npcDeltaBefore = deltas.Count(d => d.EntityId == npcId);
    bool patrol = await WaitUntil(() => deltas.Count(d => d.EntityId == npcId) >= npcDeltaBefore + 2, TimeSpan.FromSeconds(4));
    Check(patrol, "NPC 巡逻 Witness 自动广播", $"NPC 增量 {npcDeltaBefore} -> {deltas.Count(d => d.EntityId == npcId)}");

    // 5. EntitySync 位置上报 → 自身增量回发（owner 可见性）
    var sync = new EntitySync
    {
        Position = new Vector3 { X = 10, Y = 0, Z = 20 },
        Rotation = new Vector3 { X = 0, Y = 0, Z = 0 }
    };
    Send(GenIds.EntitySync, sync.Serialize(), clientSessionId);
    bool selfSync = await WaitUntil(() => deltas.Any(d => d.EntityId == clientSessionId), TimeSpan.FromSeconds(3));
    Check(selfSync, "EntitySync 自身增量回发（owner 可见）");

    // 6. ScriptAction 伤害自身：TakeDamage 10 → Hp 100→90（解析增量属性）
    var damage = new ScriptAction { EntityId = clientSessionId, Method = "TakeDamage", Args = new List<int> { 10 } };
    Send(GenIds.ScriptAction, damage.Serialize(), clientSessionId);
    bool selfDamaged = await WaitUntil(() =>
    {
        foreach (var d in deltas)
        {
            if (d.EntityId != clientSessionId) continue;
            var skel = Battle.Entities.PlayerEntityDef.Create(d.EntityId);
            Framework.Entity.PropertyCodec.DeserializeInto(skel, d.Props, applyDirty: false);
            if (skel.Get<int>("Hp") == 90) return true;
        }
        return false;
    }, TimeSpan.FromSeconds(3));
    Check(selfDamaged, "ScriptAction 玩家受击掉血（Hp=90）");

    // 7. ScriptAction 击杀 NPC：TakeDamage 100 → NPC Hp=0 增量广播
    var kill = new ScriptAction { EntityId = npcId, Method = "TakeDamage", Args = new List<int> { 100 } };
    Send(GenIds.ScriptAction, kill.Serialize(), clientSessionId);
    bool npcKilled = await WaitUntil(() =>
    {
        foreach (var d in deltas)
        {
            if (d.EntityId != npcId) continue;
            var skel = Battle.Entities.GameplayEntityDefs.Npc.CreateEntity(d.EntityId);
            Framework.Entity.PropertyCodec.DeserializeInto(skel, d.Props, applyDirty: false);
            if (skel.Get<int>("Hp") == 0) return true;
        }
        return false;
    }, TimeSpan.FromSeconds(3));
    Check(npcKilled, "ScriptAction 击杀 NPC（Hp=0 广播）");

    await Task.WhenAny(readTask, Task.Delay(1500)); // 读循环在 Close 后自行结束，这里兜底
    client.Close();
}

// ========== 第三部分：断线重连（挂起 → 恢复 → 超时离场） ==========
Console.WriteLine("== Battle 断线重连：挂起 → 恢复 → 超时离场 ==");
using (var conn2 = new TcpClient())
{
    await conn2.ConnectAsync("127.0.0.1", 31327);
    var s2 = conn2.GetStream();
    var r2 = new LengthPrefixedPacketReader();
    var b2 = new byte[8192];
    var deltas2 = new ConcurrentQueue<EntityDeltaSync>();

    var read2 = Task.Run(async () =>
    {
        while (true)
        {
            int n = await s2.ReadAsync(b2);
            if (n == 0) break;
            r2.Append(b2.AsSpan(0, n));
            while (r2.TryReadPacket(out var pkt))
            {
                if (pkt.Length < 4) continue;
                int msgId = BitConverter.ToInt32(pkt.Span.Slice(0, 4));
                if (msgId == GenIds.EntityDeltaSync)
                {
                    var d = MemoryPackSerializer.Deserialize<EntityDeltaSync>(pkt.Slice(4).Span);
                    if (d != null) deltas2.Enqueue(d);
                }
            }
        }
    });

    void Send2(int msgId, byte[] payload, long targetSessionId)
    {
        byte[] routed = Shared.RouteMetadata.AttachClientSessionId(payload, targetSessionId);
        byte[] packet = PacketBuilder.BuildPacket(msgId, routed, out int len);
        s2.Write(packet.AsSpan(0, len));
        System.Buffers.ArrayPool<byte>.Shared.Return(packet);
    }

    // 认证（模拟网关新连接）
    var auth2 = new Framework.Core.Security.InternalAuthFilter("change-this-secret", "TestGateway-127.0.0.1:2");
    byte[] authPacket2 = auth2.BuildAuthPacket();
    byte[] authFramed2 = PacketBuilder.BuildPacket(Framework.Core.Security.InternalAuthFilter.AuthMsgId, authPacket2.AsSpan(4), out int authLen2);
    s2.Write(authFramed2.AsSpan(0, authLen2));
    System.Buffers.ArrayPool<byte>.Shared.Return(authFramed2);

    // 1. 模拟网关断线通知 → 玩家实体挂起（保留场景席位）
    var disconnect = new PlayerDisconnect { ClientSessionId = 9001 };
    Send2(GenIds.PlayerDisconnect, disconnect.Serialize(), 9001);
    await Task.Delay(300);

    // 2. 重连恢复：PlayerSessionResume 取消挂起；EntitySync 的自身增量应从新连接回发（绑定已续接）
    var resume = new PlayerSessionResume { ClientSessionId = 9001 };
    Send2(GenIds.PlayerSessionResume, resume.Serialize(), 9001);
    var sync2 = new EntitySync
    {
        Position = new Vector3 { X = 30, Y = 0, Z = 40 },
        Rotation = new Vector3 { X = 0, Y = 0, Z = 0 }
    };
    Send2(GenIds.EntitySync, sync2.Serialize(), 9001);
    bool resumed = await WaitUntil(() => deltas2.Any(d => d.EntityId == 9001), TimeSpan.FromSeconds(3));
    Check(resumed, "断线重连：会话恢复 + 新连接收到自身增量");

    // 3. 二次断线 → 宽限（2s）超时 → 实体离场
    Send2(GenIds.PlayerDisconnect, disconnect.Serialize(), 9001);
    conn2.Close();
    await Task.Delay(4500);

    // 4. 新玩家加入（9002）：旧实体 9001 应已离场（快照中不存在）
    using (var conn3 = new TcpClient())
    {
        await conn3.ConnectAsync("127.0.0.1", 31327);
        var s3 = conn3.GetStream();
        var r3 = new LengthPrefixedPacketReader();
        var b3 = new byte[8192];
        var joinResults3 = new ConcurrentQueue<BattleJoinResult>();
        var snapshots3 = new ConcurrentDictionary<long, EntitySnapshot>();

        var read3 = Task.Run(async () =>
        {
            while (true)
            {
                int n = await s3.ReadAsync(b3);
                if (n == 0) break;
                r3.Append(b3.AsSpan(0, n));
                while (r3.TryReadPacket(out var pkt))
                {
                    if (pkt.Length < 4) continue;
                    int msgId = BitConverter.ToInt32(pkt.Span.Slice(0, 4));
                    var payload = pkt.Slice(4);
                    switch (msgId)
                    {
                        case GenIds.BattleJoinResult:
                            var res = MemoryPackSerializer.Deserialize<BattleJoinResult>(payload.Span);
                            if (res != null) joinResults3.Enqueue(res);
                            break;
                        case GenIds.EntitySnapshot:
                            var snap = MemoryPackSerializer.Deserialize<EntitySnapshot>(payload.Span);
                            if (snap != null) snapshots3[snap.EntityId] = snap;
                            break;
                    }
                }
            }
        });

        void Send3(int msgId, byte[] payload, long targetSessionId)
        {
            byte[] routed = Shared.RouteMetadata.AttachClientSessionId(payload, targetSessionId);
            byte[] packet = PacketBuilder.BuildPacket(msgId, routed, out int len);
            s3.Write(packet.AsSpan(0, len));
            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
        }

        var auth3 = new Framework.Core.Security.InternalAuthFilter("change-this-secret", "TestGateway-127.0.0.1:3");
        byte[] authPacket3 = auth3.BuildAuthPacket();
        byte[] authFramed3 = PacketBuilder.BuildPacket(Framework.Core.Security.InternalAuthFilter.AuthMsgId, authPacket3.AsSpan(4), out int authLen3);
        s3.Write(authFramed3.AsSpan(0, authLen3));
        System.Buffers.ArrayPool<byte>.Shared.Return(authFramed3);

        var join3 = new BattleJoin { RoomId = "PVP", SceneName = "集成测试", SceneType = "PVP", MaxPlayers = 10 };
        Send3(GenIds.BattleJoin, join3.Serialize(), 9002);
        bool joined3 = await WaitUntil(() => joinResults3.Count > 0 && snapshots3.Count >= 5, TimeSpan.FromSeconds(6));
        bool oldGone = !snapshots3.ContainsKey(9001);
        Check(joined3 && oldGone, "重连超时离场：新玩家快照不含旧实体 9001", $"快照数={snapshots3.Count}");

        await Task.WhenAny(read3, Task.Delay(1500));
        conn3.Close();
    }
    await Task.WhenAny(read2, Task.Delay(1500));
}

// ========== 第四部分：静态分片（Gateway 多 Battle 节点 + 按玩家绑定路由） ==========
Console.WriteLine("== 静态分片：Gateway 多 Battle 节点路由 ==");
{
    // 伪 Battle 节点 A/B：响应 40001/40006 战斗消息，回显节点标记
    var fakeBattleServers = new List<TcpServer>();
    void StartFakeBattleNode(int port, string marker)
    {
        var server = new TcpServer();
        server.OnDataReceived += (session, data) =>
        {
            if (data.Length < 4) return;
            int msgId = BitConverter.ToInt32(data.Span.Slice(0, 4));
            if (msgId == Framework.Core.Security.InternalAuthFilter.AuthMsgId) return; // 忽略认证握手
            if (msgId != GenIds.BattleJoin && msgId != GenIds.ScriptAction) return;    // 只响应战斗业务消息
            if (!Shared.RouteMetadata.TryExtractClientSessionId(data.Slice(4), out long clientSessionId, out _)) return;
            byte[] markerPayload = System.Text.Encoding.UTF8.GetBytes($"{marker}|{clientSessionId}");
            byte[] routed = Shared.RouteMetadata.AttachTargetSessionId(markerPayload, clientSessionId);
            byte[] packet = PacketBuilder.BuildPacket(40099, routed, out int len);
            Network.PacketSender.Send(session, packet, len);
        };
        server.StartAsync(port).GetAwaiter().GetResult();
        fakeBattleServers.Add(server);
    }
    StartFakeBattleNode(31420, "BAT-A");
    StartFakeBattleNode(31421, "BAT-B");

    // 伪 Center：响应匹配请求，分配节点 B（Battle-127.0.0.1:31421）
    // 额外支持实体迁移：CategoryId="MIGRATE:<target-node-id>" 时向 Gateway 下发 91005 切换玩家绑定
    var fakeCenter = new TcpServer();
    fakeCenter.OnDataReceived += (session, data) =>
    {
        if (data.Length < 4) return;
        int msgId = BitConverter.ToInt32(data.Span.Slice(0, 4));
        if (msgId != GenIds.CenterMatch) return;
        if (!Shared.RouteMetadata.TryExtractClientSessionId(data.Slice(4), out long clientSessionId, out var matchClean)) return;
        var matchMsg = MemoryPackSerializer.Deserialize<CenterMatch>(matchClean.AsSpan());

        // 实体迁移重绑定：只下发 91005（EntityMigrateRouted），不走普通匹配回包
        if (matchMsg != null && matchMsg.CategoryId != null && matchMsg.CategoryId.StartsWith("MIGRATE:"))
        {
            string targetNodeId = matchMsg.CategoryId.Substring("MIGRATE:".Length);
            var routedNotify = new Framework.Protocol.Generated.EntityMigrateRouted
            {
                ClientSessionId = clientSessionId,
                NewNodeId = targetNodeId
            };
            byte[] routedPayload = routedNotify.Serialize();
            byte[] routedPacket = PacketBuilder.BuildPacket(GenIds.EntityMigrateRouted, routedPayload, out int routedLen);
            Network.PacketSender.Send(session, routedPacket, routedLen);
            return;
        }

        var res = new CenterMatchResult
        {
            Success = true,
            RoomId = "shard-room",
            BattleNodeId = "Battle-127.0.0.1:31421",
            SceneId = "PVP",
            SceneType = "PVP",
            Message = "ok"
        };
        byte[] routed = Shared.RouteMetadata.AttachTargetSessionId(res.Serialize(), clientSessionId);
        byte[] packet = PacketBuilder.BuildPacket(GenIds.CenterMatchResult, routed, out int len);
        Network.PacketSender.Send(session, packet, len);
    };
    fakeCenter.StartAsync(31999).GetAwaiter().GetResult();

    // 进程内启动 Gateway（后端 Login/Game 指向死端口自动重试；Center/Battle 连伪节点）
    try
    {
        await Gateway.GatewayServerApp.StartNetworkAsync();
        Check(true, "Gateway 启动（多 Battle 节点）");
    }
    catch (Exception ex)
    {
        Check(false, "Gateway 启动", ex.Message);
        return 1;
    }

    using (var gwClient = new TcpClient())
    {
        await gwClient.ConnectAsync("127.0.0.1", 31400);
        var gws = gwClient.GetStream();
        var gwr = new LengthPrefixedPacketReader();
        var gwb = new byte[8192];
        var markers = new ConcurrentQueue<string>();

        var readGw = Task.Run(async () =>
        {
            while (true)
            {
                int n = await gws.ReadAsync(gwb);
                if (n == 0) break;
                gwr.Append(gwb.AsSpan(0, n));
                while (gwr.TryReadPacket(out var pkt))
                {
                    if (pkt.Length < 4) continue;
                    int msgId = BitConverter.ToInt32(pkt.Span.Slice(0, 4));
                    if (msgId == GenIds.CenterMatchResult)
                    {
                        markers.Enqueue("MATCH");
                    }
                    else if (msgId == 40099)
                    {
                        markers.Enqueue(System.Text.Encoding.UTF8.GetString(pkt.Slice(4).Span));
                    }
                }
            }
        });

        void SendGw(int msgId, byte[] payload)
        {
            byte[] packet = PacketBuilder.BuildPacket(msgId, payload, out int len);
            gws.Write(packet.AsSpan(0, len));
            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
        }

        await Task.Delay(1500); // 等待节点连接与认证就绪

        // 1. 无绑定 → 默认节点 A
        SendGw(GenIds.BattleJoin, new BattleJoin { RoomId = "shard-a" }.Serialize());
        bool gotA = await WaitUntil(() => markers.Any(m => m.StartsWith("BAT-A")), TimeSpan.FromSeconds(5));
        Check(gotA, "无绑定消息路由到默认节点 A");

        // 2. 匹配 → 伪 Center 分配节点 B → 客户端收到回包（Gateway 同时学习绑定）
        SendGw(GenIds.CenterMatch, new CenterMatch { CategoryId = "PVP" }.Serialize());
        bool gotMatch = await WaitUntil(() => markers.Any(m => m == "MATCH"), TimeSpan.FromSeconds(5));
        Check(gotMatch, "匹配回包到达（携带节点 B 分配）");

        // 3. 绑定生效 → 后续战斗消息路由到节点 B
        SendGw(GenIds.BattleJoin, new BattleJoin { RoomId = "shard-b" }.Serialize());
        bool gotB = await WaitUntil(() => markers.Any(m => m.StartsWith("BAT-B")), TimeSpan.FromSeconds(5));
        Check(gotB, "绑定后消息路由到节点 B");

        await Task.WhenAny(readGw, Task.Delay(1000));
        gwClient.Close();
    }
}

// ========== 第五部分：实体在线迁移（91005 协议 + Gateway 玩家 Battle 节点绑定切换） ==========
// 源/目标 Battle 由伪节点（31420/31421）扮演、Center 由伪节点（31999）扮演、Gateway 为真实实例。
// 验证：迁移成功后 Center 下发 EntityMigrateRouted(91005) → Gateway 把该玩家的战斗消息改路由到新节点。
Console.WriteLine("== 实体迁移：Gateway 接收 91005 后切换玩家 Battle 节点绑定 ==");
{
    using var migClient = new TcpClient();
    await migClient.ConnectAsync("127.0.0.1", 31400);
    var ms = migClient.GetStream();
    var mr = new LengthPrefixedPacketReader();
    var mb = new byte[8192];
    var migMarkers = new ConcurrentQueue<string>();
    var readMig = Task.Run(async () =>
    {
        while (true)
        {
            int n = await ms.ReadAsync(mb);
            if (n == 0) break;
            mr.Append(mb.AsSpan(0, n));
            while (mr.TryReadPacket(out var pkt))
            {
                if (pkt.Length < 4) continue;
                int msgId = BitConverter.ToInt32(pkt.Span.Slice(0, 4));
                if (msgId == 40099)
                {
                    migMarkers.Enqueue(System.Text.Encoding.UTF8.GetString(pkt.Slice(4).Span));
                }
            }
        }
    });

    void SendMig(int msgId, byte[] payload)
    {
        byte[] packet = PacketBuilder.BuildPacket(msgId, payload, out int len);
        ms.Write(packet.AsSpan(0, len));
        System.Buffers.ArrayPool<byte>.Shared.Return(packet);
    }

    await Task.Delay(500);

    // 1. 未绑定 → 默认节点 A
    SendMig(GenIds.BattleJoin, new BattleJoin { RoomId = "mig-default" }.Serialize());
    bool migGotA = await WaitUntil(() => migMarkers.Any(m => m.StartsWith("BAT-A")), TimeSpan.FromSeconds(5));
    Check(migGotA, "迁移前消息路由到默认节点 A");

    // 2. 模拟迁移完成：Center 向 Gateway 下发 91005，把本客户端绑定切换到节点 B
    SendMig(GenIds.CenterMatch, new CenterMatch { CategoryId = "MIGRATE:Battle-127.0.0.1:31421" }.Serialize());
    await Task.Delay(300); // 等待 91005 经伪 Center 到达 Gateway 并完成绑定切换

    // 3. 绑定切换后，后续战斗消息路由到节点 B
    SendMig(GenIds.BattleJoin, new BattleJoin { RoomId = "mig-after" }.Serialize());
    bool migGotB = await WaitUntil(() => migMarkers.Any(m => m.StartsWith("BAT-B")), TimeSpan.FromSeconds(5));
    Check(migGotB, "迁移重绑定后消息路由到节点 B");

    await Task.WhenAny(readMig, Task.Delay(1000));
    migClient.Close();
}

Console.WriteLine(failures == 0 ? "\n===== NetworkVerify 全部通过 =====" : $"\n===== NetworkVerify 失败 {failures} 项 =====");
return failures == 0 ? 0 : 1;

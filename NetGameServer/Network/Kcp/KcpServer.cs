using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Network.Kcp;

/// <summary>
/// KCP 服务器：在 UDP 之上提供可靠有序传输（对标 KBE kcp_packet_*）。
/// - 会话按键 (远端端点, conv) 区分：conv 从每个数据报的 KCP 包头首 4 字节提取，
///   客户端每次连接使用随机 conv（防固定 conv 会话固定攻击），同一 NAT 后多客户端也能区分。
/// - 收包线程：UDP Receive → KcpSession.Input → OnDataReceived（与 UdpServer 一致的事件模型）
/// - 驱动线程：周期性调用所有会话的 Update（驱动发送/重传）；与收包线程经会话内锁互斥
/// </summary>
public class KcpServer : INetworkServer
{
    /// <summary>会话总数上限（P1 洪水防护：未认证数据报不得无界建会话）。</summary>
    private const int MaxSessions = 10000;
    /// <summary>单 IP 会话数上限（P1 洪水防护：同一源地址伪造多端口/多 conv 时受限）。</summary>
    private const int MaxSessionsPerIp = 64;

    /// <summary>会话键：远端端点 + KCP 转换号。</summary>
    private readonly record struct SessionKey(IPEndPoint EndPoint, uint Conv);

    private UdpClient? udpClient;
    private CancellationTokenSource? cts;
    private readonly ConcurrentDictionary<SessionKey, KcpSession> sessions = new();
    // P3 修复：单 IP 会话计数表（替代每次新建会话时 O(n) 全表扫描统计 per-IP 数）。
    private readonly ConcurrentDictionary<IPAddress, int> sessionsPerIp = new();
    private readonly TimeSpan sessionTimeout = TimeSpan.FromMinutes(5);
    // P2 修复：清理窗口改为 Interlocked 抢占的 tick，供接收线程与驱动线程并发调用
    private const long CleanupIntervalTicks = 30L * TimeSpan.TicksPerSecond;
    private long nextCleanupTicks = DateTime.UtcNow.AddSeconds(30).Ticks;
    // P2 加固：接收循环告警限频（防止恶意洪泛触发日志风暴）。
    private readonly object warnGate = new();
    private DateTime lastShortWarnUtc = DateTime.MinValue;
    private DateTime lastSessionCapWarnUtc = DateTime.MinValue;
    private DateTime lastPerIpCapWarnUtc = DateTime.MinValue;
    private DateTime lastInputErrorWarnUtc = DateTime.MinValue;
    private DateTime lastViolationWarnUtc = DateTime.MinValue;

    public event SessionConnectedHandler? OnSessionConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnSessionDisconnected;

    public Task StartAsync(int port)
    {
        try
        {
            udpClient = new UdpClient(port);
            cts = new CancellationTokenSource();
            Shared.Log.Info($"[KcpServer] 启动成功，监听端口:{port}（conv 按连接随机，自数据包提取）");

            _ = ReceiveLoopAsync(cts.Token);
            _ = DriveLoopAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Shared.Log.Error($"[KcpServer] 启动失败 Port:{port} Exception:{ex}");
            throw;
        }
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && udpClient != null)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udpClient.ReceiveAsync(token);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset
                    or SocketError.ConnectionAborted or SocketError.NetworkReset)
                {
                    // UDP 无连接语义：对端关闭/不存在时收到的 ICMP 错误（ECONNRESET 10054 / ECONNABORTED 10053 /
                    // ENETRESET 10052）会令下一次 ReceiveAsync 抛 SocketException——**属正常事件**，
                    // 必须忽略并继续接收。修复前此异常落到 while 外的 catch(Exception) → 整个 KCP 端口停服
                    // （表现为客户端集体掉线且无告警，UDP 端口对等实现 UdpServer 已按每报隔离处理）。
                    if (ShouldLogWarning(ref lastShortWarnUtc))
                        Shared.Log.Warning($"[KcpServer] UDP 对端重置（ECONNRESET，忽略继续）:{ex.Message}");
                    continue;
                }

                // KCP 每个分段的包头首 4 字节即 conv，据此区分连接
                if (result.Buffer.Length < 4)
                {
                    if (ShouldLogWarning(ref lastShortWarnUtc))
                        Shared.Log.Warning($"[KcpServer] 数据报过短(<4 字节)，丢弃 Remote:{result.RemoteEndPoint}");
                    continue;
                }
                uint conv = BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer);
                var key = new SessionKey(result.RemoteEndPoint, conv);

                if (!sessions.TryGetValue(key, out var session))
                {
                    // 洪水防护：未认证数据报只允许建立有界数量的会话
                    if (sessions.Count >= MaxSessions)
                    {
                        if (ShouldLogWarning(ref lastSessionCapWarnUtc))
                            Shared.Log.Warning($"[KcpServer] 会话数已达上限({MaxSessions})，拒绝新会话 Remote:{result.RemoteEndPoint}");
                        continue;
                    }
                    // P3 修复：O(1) 查 per-IP 计数（原实现遍历全部会话统计，洪泛时 O(n²)）。
                    int perIp = sessionsPerIp.TryGetValue(result.RemoteEndPoint.Address, out int c) ? c : 0;
                    if (perIp >= MaxSessionsPerIp)
                    {
                        if (ShouldLogWarning(ref lastPerIpCapWarnUtc))
                            Shared.Log.Warning($"[KcpServer] 每 IP 会话数已达上限({MaxSessionsPerIp})，拒绝新会话 Remote:{result.RemoteEndPoint}");
                        continue;
                    }

                    session = new KcpSession(udpClient, key.EndPoint, key.Conv);
                    session.OnDataReceived += (s, data) => OnDataReceived?.Invoke(s, data);
                    sessions[key] = session;
                    sessionsPerIp.AddOrUpdate(result.RemoteEndPoint.Address, 1, (_, v) => v + 1);
                    Shared.Log.Info($"[KcpServer] 新会话建立 SessionId:{session.SessionId} Remote:{key.EndPoint} conv=0x{key.Conv:X8}");
                    // P2 修复（重要）：回调必须隔离——本方法的 try/catch 包住整个 while，
                    // 而这里是“裸调用”：宿主回调抛一次异常就会被外层 catch 吞掉 → **KCP 接收循环直接结束**，
                    // 此后该端口永不再收包（仅一行 Warning），而 TCP/UDP/WebSocket 仍正常 →
                    // 表现为“部分客户端集体掉线且无告警”，极难定位。（对照 UdpServer 的每报隔离结构）
                    try
                    {
                        OnSessionConnected?.Invoke(session);
                    }
                    catch (Exception ex)
                    {
                        Shared.Log.Warning($"[KcpServer] 会话连接回调异常（已隔离，循环继续）SessionId:{session.SessionId} Exception:{ex.Message}");
                    }
                }

                try
                {
                    session.Input(result.Buffer);
                }
                catch (Exception ex)
                {
                    // P2 加固：协议异常（如 frg>=128 分片毒化）逐包抛异常；限频告警并把会话标记为待关闭，
                    // 既防日志风暴，又防止攻击者不断重放异常分片驱动无限处理。
                    if (ShouldLogWarning(ref lastInputErrorWarnUtc))
                        Shared.Log.Warning($"[KcpServer] 会话数据处理异常 SessionId:{session.SessionId} Exception:{ex.Message}");
                    session.MarkedForClose = true;
                }

                // P2 加固：会话被标记为待关闭（超大消息/协议异常）时立即移除并关闭，防止死链路残留。
                if (session.MarkedForClose)
                {
                    // P2 修复：仅在**确实移除成功**时扣减 per-IP 计数。
                    // 原实现不判返回值（并发下 StopAsync/CleanupIfNeeded 已移除）→ 重复扣减，per-IP 上限可被提前清零。
                    bool removed = sessions.TryRemove(key, out _);
                    try { session.Close(); } catch { /* 关闭异常忽略 */ }
                    if (removed)
                    {
                        DecrementPerIp(key.EndPoint.Address);
                    }
                    // P2 修复：原为无条件 Warning；攻击者用“每包一个新会话”即可驱动无上限日志行数（日志风暴），
                    // 使真正的告警被淹没。改为限频（与其它同类告警一致）。
                    if (ShouldLogWarning(ref lastViolationWarnUtc))
                        Shared.Log.Warning($"[KcpServer] 移除异常/超大消息会话 SessionId:{session.SessionId} Remote:{key.EndPoint}（同类告警 5s 最多一次）");
                    // P2 修复（重要）：回调必须隔离——本方法的 try/catch 包住整个 while，
                    // 而这里是“裸调用”：宿主回调（Gateway 的断开处理器首行即解引用 session.SessionId）
                    // 抛一次异常就会被外层 catch 吞掉 → **KCP 接收循环直接结束**，
                    // 此后该端口永不再收包（仅一行 Warning），而 TCP/UDP/WebSocket 仍正常 →
                    // 表现为“部分客户端集体掉线且无告警”，极难定位。（对照 UdpServer 的每报隔离结构）
                    try
                    {
                        OnSessionDisconnected?.Invoke(session, "KCP protocol violation.");
                    }
                    catch (Exception ex)
                    {
                        Shared.Log.Warning($"[KcpServer] 会话断开回调异常（已隔离，循环继续）SessionId:{session.SessionId} Exception:{ex.Message}");
                    }
                }

                CleanupIfNeeded();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            // P2 说明：本 catch 在 while **之外**，一旦命中则接收循环已终止（KCP 端口不再收包）。
            // 已知可抛点（两个服务器事件回调）已就地隔离，此处保留为兵底；
            // 若有异常到达这里，必须用 Error + 明确措辞让运维能立即定位“KCP 已停服”。
            Shared.Log.Error(ex, "[KcpServer] 接收循环异常终止（KCP 端口将不再收包，需重启或排查）");
        }
    }

    /// <summary>每 10ms 驱动所有会话的 KCP Update（发送/重传）。</summary>
    private async Task DriveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(10, token);
                foreach (var session in sessions.Values)
                {
                    try
                    {
                        session.Update();
                    }
                    catch (Exception ex)
                    {
                        Shared.Log.Warning($"[KcpServer] 会话驱动异常 SessionId:{session.SessionId} Exception:{ex.Message}");
                    }
                }
                // P2 修复：清理不再只由“收到数据报”驱动。原先唯一调用点在收包循环内，
                // 若一段时间没有任何数据报（单机部署/客户端集体静默掉线），死会话永不回收、
                // OnSessionDisconnected 不触发 → Gateway 的断线/挂起上报（重连宽限期、Center 挂起目录）不落地。
                // 内部有 30s Interlocked 窗口，10ms 调用无实际开销。
                CleanupIfNeeded();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CleanupIfNeeded()
    {
        // P2 修复：清理窗口改用 Interlocked 抢占（接收线程 + 驱动线程会并发调用），
        // 避免两处同时做全表扫描。
        long nowTicks = DateTime.UtcNow.Ticks;
        long next = Volatile.Read(ref nextCleanupTicks);
        if (nowTicks < next) return;
        if (Interlocked.CompareExchange(ref nextCleanupTicks, nowTicks + CleanupIntervalTicks, next) != next) return;

        var now = DateTime.UtcNow;
        foreach (var pair in sessions.ToArray())
        {
            if (now - pair.Value.LastActivityTime <= sessionTimeout) continue;

            // P2 修复：TryRemove 的返回值必须判。并发下（如 StopAsync 已清空 sessions 而接收线程仍在跑）
            // 移除可能失败，此时 session 为 null，原实现会：
            //   ① 重复扣减 per-IP 计数（可被用来把洪水防护上限提前清零）
            //   ② 把 null 会话抛给宿主 → Gateway 回调首行解引用 session.SessionId 抛 NRE
            //      → 进而触发收包循环的“永久停摆”（两条缺陷叠加成完整故障链）。
            if (!sessions.TryRemove(pair.Key, out var session) || session == null)
            {
                continue;
            }
            DecrementPerIp(pair.Key.EndPoint.Address);
            try { session.Close(); } catch { /* 关闭异常忽略 */ }
            Shared.Log.Warning($"[KcpServer] 会话超时断开 SessionId:{session.SessionId} Remote:{pair.Key.EndPoint} TimeoutSeconds:{sessionTimeout.TotalSeconds}");
            try
            {
                OnSessionDisconnected?.Invoke(session, "KCP session timeout.");
            }
            catch (Exception ex)
            {
                Shared.Log.Warning($"[KcpServer] 会话断开回调异常（已隔离）SessionId:{session.SessionId} Exception:{ex.Message}");
            }
        }
    }

    private void DecrementPerIp(IPAddress address)
    {
        if (!sessionsPerIp.TryGetValue(address, out int v)) return;
        if (v <= 1)
        {
            sessionsPerIp.TryRemove(address, out _);
        }
        else
        {
            sessionsPerIp.TryUpdate(address, v - 1, v);
        }
    }

    /// <summary>接收循环告警限频（P2 加固）：同类告警每 <paramref name="minIntervalSeconds"/> 秒最多输出一次。</summary>
    private bool ShouldLogWarning(ref DateTime lastUtc, int minIntervalSeconds = 5)
    {
        lock (warnGate)
        {
            var now = DateTime.UtcNow;
            if ((now - lastUtc).TotalSeconds < minIntervalSeconds) return false;
            lastUtc = now;
            return true;
        }
    }

    public Task StopAsync()
    {
        Shared.Log.Info("[KcpServer] 停止监听。");
        cts?.Cancel();
        udpClient?.Close();
        udpClient?.Dispose();
        udpClient = null;

        foreach (var session in sessions.Values)
        {
            // P2 修复：回调隔离——宿主回调抛异常不得中断 StopAsync（否则 sessions.Clear 不会执行）
            try
            {
                OnSessionDisconnected?.Invoke(session, "Server stopped.");
            }
            catch (Exception ex)
            {
                Shared.Log.Warning($"[KcpServer] 停机断开回调异常（已隔离）SessionId:{session.SessionId} Exception:{ex.Message}");
            }
            session.Close();
        }
        sessions.Clear();
        sessionsPerIp.Clear();
        return Task.CompletedTask;
    }
}

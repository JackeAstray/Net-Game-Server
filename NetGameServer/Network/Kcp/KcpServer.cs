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
    private DateTime nextCleanupAt = DateTime.UtcNow.AddSeconds(30);

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
                var result = await udpClient.ReceiveAsync(token);

                // KCP 每个分段的包头首 4 字节即 conv，据此区分连接
                if (result.Buffer.Length < 4)
                {
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
                        Shared.Log.Warning($"[KcpServer] 会话数已达上限({MaxSessions})，拒绝新会话 Remote:{result.RemoteEndPoint}");
                        continue;
                    }
                    // P3 修复：O(1) 查 per-IP 计数（原实现遍历全部会话统计，洪泛时 O(n²)）。
                    int perIp = sessionsPerIp.TryGetValue(result.RemoteEndPoint.Address, out int c) ? c : 0;
                    if (perIp >= MaxSessionsPerIp)
                    {
                        Shared.Log.Warning($"[KcpServer] 每 IP 会话数已达上限({MaxSessionsPerIp})，拒绝新会话 Remote:{result.RemoteEndPoint}");
                        continue;
                    }

                    session = new KcpSession(udpClient, key.EndPoint, key.Conv);
                    session.OnDataReceived += (s, data) => OnDataReceived?.Invoke(s, data);
                    sessions[key] = session;
                    sessionsPerIp.AddOrUpdate(result.RemoteEndPoint.Address, 1, (_, v) => v + 1);
                    Shared.Log.Info($"[KcpServer] 新会话建立 SessionId:{session.SessionId} Remote:{key.EndPoint} conv=0x{key.Conv:X8}");
                    OnSessionConnected?.Invoke(session);
                }

                try
                {
                    session.Input(result.Buffer);
                }
                catch (Exception ex)
                {
                    Shared.Log.Warning($"[KcpServer] 会话数据处理异常 SessionId:{session.SessionId} Exception:{ex.Message}");
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
            Shared.Log.Warning($"[KcpServer] 接收循环异常 Exception:{ex}");
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
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CleanupIfNeeded()
    {
        if (DateTime.UtcNow < nextCleanupAt) return;
        nextCleanupAt = DateTime.UtcNow.AddSeconds(30);

        var now = DateTime.UtcNow;
        foreach (var pair in sessions)
        {
            if (now - pair.Value.LastActivityTime <= sessionTimeout) continue;

            sessions.TryRemove(pair.Key, out var session);
            DecrementPerIp(pair.Key.EndPoint.Address);
            session?.Close();
            Shared.Log.Warning($"[KcpServer] 会话超时断开 SessionId:{pair.Value.SessionId} Remote:{pair.Key.EndPoint} TimeoutSeconds:{sessionTimeout.TotalSeconds}");
            OnSessionDisconnected?.Invoke(session!, "KCP session timeout.");
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

    public Task StopAsync()
    {
        Shared.Log.Info("[KcpServer] 停止监听。");
        cts?.Cancel();
        udpClient?.Close();
        udpClient?.Dispose();
        udpClient = null;

        foreach (var session in sessions.Values)
        {
            OnSessionDisconnected?.Invoke(session, "Server stopped.");
            session.Close();
        }
        sessions.Clear();
        sessionsPerIp.Clear();
        return Task.CompletedTask;
    }
}

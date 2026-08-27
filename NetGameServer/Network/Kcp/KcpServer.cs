using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Network.Kcp;

/// <summary>
/// KCP 服务器：在 UDP 之上提供可靠有序传输（对标 KBE kcp_packet_*）。
/// - 每个远端端点一个 KcpSession
/// - 收包线程：UDP Receive → KcpSession.Input → OnDataReceived（与 UdpServer 一致的事件模型）
/// - 驱动线程：周期性调用所有会话的 Update（驱动发送/重传）
/// </summary>
public class KcpServer : INetworkServer
{
    /// <summary>KCP 转换号（同一 UDP 端口上的所有会话共用；由远端端点区分）。</summary>
    private const uint DefaultConv = 0x4B434550; // "KCEP"

    private UdpClient? udpClient;
    private CancellationTokenSource? cts;
    private readonly ConcurrentDictionary<IPEndPoint, KcpSession> sessions = new();
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
            Shared.Log.Info($"[KcpServer] 启动成功，监听端口:{port} (KCP conv=0x{DefaultConv:X8})");

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

                var session = sessions.GetOrAdd(result.RemoteEndPoint, ep =>
                {
                    var newSession = new KcpSession(udpClient, ep, DefaultConv);
                    newSession.OnDataReceived += (s, data) => OnDataReceived?.Invoke(s, data);
                    Shared.Log.Info($"[KcpServer] 新会话建立 SessionId:{newSession.SessionId} Remote:{ep}");
                    OnSessionConnected?.Invoke(newSession);
                    return newSession;
                });

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
            session?.Close();
            Shared.Log.Warning($"[KcpServer] 会话超时断开 SessionId:{pair.Value.SessionId} Remote:{pair.Key} TimeoutSeconds:{sessionTimeout.TotalSeconds}");
            OnSessionDisconnected?.Invoke(session!, "KCP session timeout.");
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
        return Task.CompletedTask;
    }
}

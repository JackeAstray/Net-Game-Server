using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Network.Routing;

namespace Network.Udp;

/// <summary>
/// 一个简单的 UDP 服务器实现，可作为 KCP 等上层协议的基础。
/// 此类负责在指定端口上接收 UDP 数据报，并提供启动/停止生命周期方法。
/// </summary>
public class UdpServer : INetworkServer
{
    /// <summary>会话总数上限（P1 洪水防护：未认证数据报不得无界建会话）。</summary>
    private const int MaxSessions = 10000;
    /// <summary>单 IP 会话数上限（P1 洪水防护）。</summary>
    private const int MaxSessionsPerIp = 64;

    private UdpClient? udpClient;

    public event SessionConnectedHandler? OnSessionConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnSessionDisconnected;

    public Task StartAsync(int port)
    {
        try
        {
            udpClient = new UdpClient(port);
            Shared.Log.Info($"[UdpServer] 启动成功，监听端口:{port}");
            // 启动后台接收循环（不等待），用于持续接收传入的数据报
            _ = ReceiveLoopAsync();
        }
        catch (Exception ex)
        {
            Shared.Log.Error($"[UdpServer] 启动失败 Port:{port} Exception:{ex}");
            throw;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 循环接收来自 UDP 客户端的数据报，按远程端点维护逻辑会话，组装长度前缀分包并触发会话连接、数据接收及会话断开事件。
    /// </summary>
    /// <remarks>为每个远程端点维护 UdpSession 和 LengthPrefixedPacketReader；定期（每 30 秒）清理超过 5 分钟未活动的会话。遇到
    /// ObjectDisposedException 退出循环，其它异常记录后继续；服务器停止时触发所有存活会话的断开事件。</remarks>
    /// <returns>表示接收循环结束时完成的任务。</returns>
    private async Task ReceiveLoopAsync()
    {
        // UDP中我们往往需要通过远程地址来标识某一个会话，这里简单使用字典存储当前存在的逻辑会话。
        // 在正式复杂的实现中，应该带上心跳检测等过期清理机制。
        var sessions = new Dictionary<IPEndPoint, UdpSession>();
        var packetReaders = new Dictionary<IPEndPoint, LengthPrefixedPacketReader>();
        TimeSpan sessionTimeout = TimeSpan.FromMinutes(5);
        DateTime nextCleanupAt = DateTime.UtcNow.AddSeconds(30);

        while (udpClient != null)
        {
            try
            {
                var result = await udpClient.ReceiveAsync();

                if (!sessions.TryGetValue(result.RemoteEndPoint, out var session))
                {
                    // 洪水防护：未认证数据报只允许建立有界数量的会话
                    if (sessions.Count >= MaxSessions)
                    {
                        Shared.Log.Warning($"[UdpServer] 会话数已达上限({MaxSessions})，拒绝新会话 Remote:{result.RemoteEndPoint}");
                        continue;
                    }
                    int perIp = sessions.Keys.Count(k => k.Address.Equals(result.RemoteEndPoint.Address));
                    if (perIp >= MaxSessionsPerIp)
                    {
                        Shared.Log.Warning($"[UdpServer] 每 IP 会话数已达上限({MaxSessionsPerIp})，拒绝新会话 Remote:{result.RemoteEndPoint}");
                        continue;
                    }

                    session = new UdpSession(udpClient, result.RemoteEndPoint);
                    sessions[result.RemoteEndPoint] = session;
                    packetReaders[result.RemoteEndPoint] = new LengthPrefixedPacketReader();
                    Shared.Log.Info($"[UdpServer] 新会话建立 SessionId:{session.SessionId} Remote:{result.RemoteEndPoint}");
                    OnSessionConnected?.Invoke(session);
                }

                Shared.Log.Debug($"[UdpServer] 接收数据报 SessionId:{session.SessionId} Remote:{result.RemoteEndPoint} DatagramLength:{result.Buffer.Length}");

                var packetReader = packetReaders[result.RemoteEndPoint];
                packetReader.Append(result.Buffer);
                try
                {
                    int packetCount = 0;
                    while (packetReader.TryReadPacket(out var packet))
                    {
                        packetCount++;
                        Shared.Log.Debug($"[UdpServer] 完整分包 SessionId:{session.SessionId} Remote:{result.RemoteEndPoint} PacketLength:{packet.Length}");
                        OnDataReceived?.Invoke(session, packet);
                    }

                    if (packetCount == 0)
                    {
                        Shared.Log.Debug($"[UdpServer] 当前数据报未形成完整包 SessionId:{session.SessionId} Remote:{result.RemoteEndPoint}");
                    }

                    // 仅当该数据报被正常消费后才刷新活动时间，避免恶意数据报无限续活会话
                    session.LastActivityTime = DateTime.UtcNow;
                }
                catch (InvalidDataException ex)
                {
                    // 帧毒化防护（P1）：坏长度前缀（≤0 或 >64KB）会让 TryReadPacket 抛异常并把毒字节留在缓冲区；
                    // 若不处理，该端点后续所有数据报都会再次抛异常且 LastActivityTime 仍被刷新 → 会话永久卡死且永不超时。
                    // 重置解析器丢弃毒字节，合法客户端下一个数据报即可恢复。
                    Shared.Log.Warning($"[UdpServer] 数据报帧解析失败，重置端点解析器 Remote:{result.RemoteEndPoint} Exception:{ex.Message}");
                    packetReaders[result.RemoteEndPoint] = new LengthPrefixedPacketReader();
                }

                if (DateTime.UtcNow >= nextCleanupAt)
                {
                    DateTime now = DateTime.UtcNow;
                    foreach (var pair in sessions.ToArray())
                    {
                        if (now - pair.Value.LastActivityTime <= sessionTimeout)
                        {
                            continue;
                        }

                        sessions.Remove(pair.Key);
                        packetReaders.Remove(pair.Key);
                        Shared.Log.Warning($"[UdpServer] 会话超时断开 SessionId:{pair.Value.SessionId} Remote:{pair.Value.RemoteEndPoint} TimeoutSeconds:{sessionTimeout.TotalSeconds}");
                        OnSessionDisconnected?.Invoke(pair.Value, "UDP session timeout.");
                    }

                    nextCleanupAt = now.AddSeconds(30);
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Shared.Log.Warning($"[UdpServer] 接收循环异常 Exception:{ex}");
            }
        }

        // 服务器停止时触发所有存活 UDP 逻辑会话断开。
        foreach (var session in sessions.Values)
        {
            Shared.Log.Info($"[UdpServer] 服务停止触发会话断开 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
            OnSessionDisconnected?.Invoke(session, "Server stopped.");
        }
    }

    public Task StopAsync()
    {
        Shared.Log.Info("[UdpServer] 停止监听。");
        // 关闭并释放 UdpClient，停止接收循环
        udpClient?.Close();
        udpClient?.Dispose();
        udpClient = null;
        return Task.CompletedTask;
    }
}
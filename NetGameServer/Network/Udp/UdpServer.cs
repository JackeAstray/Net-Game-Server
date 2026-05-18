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
    private UdpClient? udpClient;

    public event SessionConnectedHandler? OnSessionConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnSessionDisconnected;

    public Task StartAsync(int port)
    {
        try
        {
            udpClient = new UdpClient(port);
            // 启动后台接收循环（不等待），用于持续接收传入的数据报
            _ = ReceiveLoopAsync();
        }
        catch (Exception ex)
        {
            Shared.Log.Error($"[UdpServer] 启动失败: {ex.Message}");
            throw;
        }
        return Task.CompletedTask;
    }

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
                    session = new UdpSession(udpClient, result.RemoteEndPoint);
                    sessions[result.RemoteEndPoint] = session;
                    packetReaders[result.RemoteEndPoint] = new LengthPrefixedPacketReader();
                    OnSessionConnected?.Invoke(session);
                }

                session.LastActivityTime = DateTime.UtcNow;

                var packetReader = packetReaders[result.RemoteEndPoint];
                packetReader.Append(result.Buffer);
                while (packetReader.TryReadPacket(out var packet))
                {
                    OnDataReceived?.Invoke(session, packet);
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
                Console.WriteLine($"Error receiving UDP data: {ex.Message}");
            }
        }

        // 服务器停止时触发所有存活 UDP 逻辑会话断开。
        foreach (var session in sessions.Values)
        {
            OnSessionDisconnected?.Invoke(session, "Server stopped.");
        }
    }

    public Task StopAsync()
    {
        // 关闭并释放 UdpClient，停止接收循环
        udpClient?.Close();
        udpClient?.Dispose();
        udpClient = null;
        return Task.CompletedTask;
    }
}
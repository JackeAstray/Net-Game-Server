using System.Net;
using System.Net.Sockets;

using Microsoft.IO;

namespace Network.Tcp;

/// <summary>
/// 一个简单的 TCP 服务器实现。
/// 此类负责在指定端口上监听传入的 TCP 连接，并为每个连接启动处理任务。
/// 提供启动/停止生命周期方法，以及基础的客户端接收循环。
/// </summary>
public class TcpServer : INetworkServer
{
    private TcpListener? tcpListener;
    private static readonly RecyclableMemoryStreamManager memoryStreamManager = new RecyclableMemoryStreamManager();

    public event SessionConnectedHandler? OnSessionConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnSessionDisconnected;

    public Task StartAsync(int port)
    {
        try
        {
            tcpListener = new TcpListener(IPAddress.Any, port);
            tcpListener.Start();

            // 启动接受客户端连接的后台循环（不等待）
            _ = AcceptClientsAsync();
        }
        catch (Exception ex)
        {
            Shared.Log.Error($"[TcpServer] 启动失败: {ex.Message}");
            throw;
        }

        return Task.CompletedTask;
    }

    private async Task AcceptClientsAsync()
    {
        while (tcpListener != null)
        {
            try
            {
                var client = await tcpListener.AcceptTcpClientAsync();
                // 接受到新的客户端后，启动异步处理任务
                _ = HandleClientAsync(client);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        var session = new TcpSession(client);
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            OnSessionConnected?.Invoke(session);

            try
            {
                while (client.Connected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    // 使用池化的 MemoryStream 来避免频繁的内存分配
                    using var ms = memoryStreamManager.GetStream();
                    ms.Write(buffer, 0, bytesRead);
                    var data = ms.GetBuffer();

                    OnDataReceived?.Invoke(session, data);
                }
            }
            catch (Exception ex)
            {
                OnSessionDisconnected?.Invoke(session, ex.Message);
                return;
            }

            OnSessionDisconnected?.Invoke(session, "客户端主动关闭了连接。");
        }
    }

    public Task StopAsync()
    {
        tcpListener?.Stop();
        tcpListener = null;
        return Task.CompletedTask;
    }
}
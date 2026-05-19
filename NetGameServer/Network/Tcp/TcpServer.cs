using System.Net;
using System.Net.Sockets;

using Network.Routing;

namespace Network.Tcp;

/// <summary>
/// 一个简单的 TCP 服务器实现。
/// 此类负责在指定端口上监听传入的 TCP 连接，并为每个连接启动处理任务。
/// 提供启动/停止生命周期方法，以及基础的客户端接收循环。
/// </summary>
public class TcpServer : INetworkServer
{
    private TcpListener? tcpListener;

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

    /// <summary>
    /// 异步接受传入的 TCP 客户端连接，并为每个连接启动独立的处理任务。
    /// </summary>
    /// <remarks>对每个接入的 TcpClient 调用 HandleClientAsync 且不等待其完成（以后台方式运行）。当 tcpListener 被释放时捕获
    /// ObjectDisposedException 并退出循环；确保 tcpListener 在使用期间已正确初始化并在停止时释放。</remarks>
    /// <returns>表示接受循环完成的异步任务；当底层侦听器被释放或停止时完成。</returns>
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

    /// <summary>
    /// 异步处理已连接的 TcpClient，按长度前缀解析数据包并在接收数据或会话状态变化时触发相应事件。
    /// </summary>
    /// <remarks>在处理期间会触发 OnSessionConnected、OnDataReceived 和 OnSessionDisconnected。使用
    /// LengthPrefixedPacketReader 解析数据包；在发生异常或对端关闭连接时会触发断开事件并释放 TcpClient。</remarks>
    /// <param name="client">要处理的已连接 TcpClient 实例。</param>
    /// <returns>表示会话处理完成的异步任务。</returns>
    private async Task HandleClientAsync(TcpClient client)
    {
        var session = new TcpSession(client);
        var packetReader = new LengthPrefixedPacketReader();

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

                    packetReader.Append(buffer.AsSpan(0, bytesRead));
                    while (packetReader.TryReadPacket(out var packet))
                    {
                        OnDataReceived?.Invoke(session, packet);
                    }
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

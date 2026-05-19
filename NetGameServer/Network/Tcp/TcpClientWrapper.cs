using System;
using System.Net.Sockets;
using System.Threading.Tasks;

using Network.Routing;

namespace Network.Tcp;

/// <summary>
/// 一个简单的 TCP 客户端实现。
/// 用于不同服务器之间的内部通讯连接，并支持断线重连。
/// </summary>
public class TcpClientWrapper : INetworkClient
{
    private TcpClient? tcpClient;
    private TcpSession? session;
    private readonly string host;
    private readonly int port;
    private bool isRunning;

    public event SessionConnectedHandler? OnConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnDisconnected;

    public TcpClientWrapper(string host, int port)
    {
        this.host = host;
        this.port = port;
    }

    /// <summary>
    /// 异步连接到指定主机和端口；成功时创建 TcpSession 并触发 OnConnected，然后处理连接；失败或断开时每 3 秒重试，直到停止运行。
    /// </summary>
    /// <remarks>设置 isRunning 为 true；在连接过程中记录日志并捕获异常（仅记录警告）；在每次失败后等待 3 秒重试；调用 HandleConnectionAsync
    /// 以处理已建立的会话。</remarks>
    /// <returns>表示异步操作完成的任务；在停止运行后（isRunning 为 false）完成。</returns>
    public async Task ConnectAsync()
    {
        isRunning = true;

        while (isRunning)
        {
            try
            {
                Shared.Log.Info($"正在连接到 {host}:{port} ...");
                tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(host, port);

                session = new TcpSession(tcpClient);
                OnConnected?.Invoke(session);

                await HandleConnectionAsync();
            }
            catch (Exception ex)
            {
                Shared.Log.Warning($"连接 {host}:{port} 失败或断开: {ex.Message}。3秒后准备重连...");
            }

            if (isRunning)
            {
                await Task.Delay(3000); // 3秒后重连
            }
        }
    }

    /// <summary>
    /// 处理 TCP 连接的异步循环：读取长度前缀包，更新会话最后活动时间，并为每个接收的数据包触发 OnDataReceived；在错误或连接关闭时触发 OnDisconnected。
    /// </summary>
    /// <remarks>在 tcpClient 为 null、未连接或 session 为 null 时立即返回。使用 LengthPrefixedPacketReader
    /// 聚合接收缓冲区并解析完整包；每次读取更新 session.LastActivityTime。捕获异常并在异常或连接关闭时触发 OnDisconnected；在退出前释放 tcpClient。缓冲区大小为 4096
    /// 字节。</remarks>
    /// <returns>表示异步操作的任务。</returns>
    private async Task HandleConnectionAsync()
    {
        if (tcpClient == null || !tcpClient.Connected || session == null)
            return;

        var packetReader = new LengthPrefixedPacketReader();

        using (tcpClient)
        {
            var stream = tcpClient.GetStream();
            var buffer = new byte[4096];

            try
            {
                while (tcpClient.Connected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    session.LastActivityTime = DateTime.UtcNow;
                    packetReader.Append(buffer.AsSpan(0, bytesRead));

                    while (packetReader.TryReadPacket(out var packet))
                    {
                        OnDataReceived?.Invoke(session, packet);
                    }
                }
            }
            catch (Exception ex)
            {
                OnDisconnected?.Invoke(session, ex.Message);
                return;
            }

            OnDisconnected?.Invoke(session, "连接关闭");
        }
    }

    public void Send(ReadOnlyMemory<byte> data)
    {
        session?.Send(data);
    }

    public void Stop()
    {
        isRunning = false;
        session?.Close();
        tcpClient?.Close();
    }
}
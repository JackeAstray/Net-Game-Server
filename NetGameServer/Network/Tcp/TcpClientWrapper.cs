using System;
using System.Net.Sockets;
using System.Threading.Tasks;

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

    private async Task HandleConnectionAsync()
    {
        if (tcpClient == null || !tcpClient.Connected || session == null)
            return;

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

                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);

                    OnDataReceived?.Invoke(session, data);
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

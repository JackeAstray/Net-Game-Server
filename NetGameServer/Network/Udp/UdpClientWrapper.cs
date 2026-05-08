using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Network.Udp;

/// <summary>
/// 一个简单的 UDP 客户端实现。
/// </summary>
public class UdpClientWrapper : INetworkClient
{
    private UdpClient? udpClient;
    private UdpSession? session;
    private readonly string host;
    private readonly int port;
    private bool isRunning;

    public event SessionConnectedHandler? OnConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnDisconnected;

    public UdpClientWrapper(string host, int port)
    {
        this.host = host;
        this.port = port;
    }

    public async Task ConnectAsync()
    {
        isRunning = true;

        try
        {
            Shared.Log.Info($"正在连接到UDP {host}:{port} ...");
            udpClient = new UdpClient();
            udpClient.Connect(host, port);

            IPAddress ipAddress;
            if (!IPAddress.TryParse(host, out ipAddress!))
            {
                var hostEntry = await Dns.GetHostEntryAsync(host);
                if (hostEntry.AddressList.Length > 0)
                {
                    ipAddress = hostEntry.AddressList[0];
                }
                else
                {
                    throw new Exception($"无法解析主机名: {host}");
                }
            }
            var remoteEndPoint = new IPEndPoint(ipAddress, port);
            session = new UdpSession(udpClient, remoteEndPoint);
            OnConnected?.Invoke(session);

            await HandleConnectionAsync();
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"UDP连接 {host}:{port} 失败: {ex.Message}");
            OnDisconnected?.Invoke(session!, ex.Message);
        }
    }

    private async Task HandleConnectionAsync()
    {
        if (udpClient == null || session == null)
            return;

        try
        {
            while (isRunning)
            {
                var result = await udpClient.ReceiveAsync();
                session.LastActivityTime = DateTime.UtcNow;
                OnDataReceived?.Invoke(session, result.Buffer);
            }
        }
        catch (Exception ex)
        {
            if (isRunning)
            {
                OnDisconnected?.Invoke(session, ex.Message);
            }
        }
    }

    public void Send(ReadOnlyMemory<byte> data)
    {
        session?.Send(data);
    }

    public void Stop()
    {
        isRunning = false;
        udpClient?.Close();
        if (session != null)
        {
            OnDisconnected?.Invoke(session, "主动停止");
        }
    }
}

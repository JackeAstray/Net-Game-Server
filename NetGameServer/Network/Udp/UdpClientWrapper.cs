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
    private UdpClient? _udpClient;
    private UdpSession? _session;
    private readonly string _host;
    private readonly int _port;
    private bool _isRunning;

    public event SessionConnectedHandler? OnConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnDisconnected;

    public UdpClientWrapper(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task ConnectAsync()
    {
        _isRunning = true;

        try
        {
            Shared.Log.Info($"正在连接到UDP {_host}:{_port} ...");
            _udpClient = new UdpClient();
            _udpClient.Connect(_host, _port);

            IPAddress ipAddress;
            if (!IPAddress.TryParse(_host, out ipAddress!))
            {
                var hostEntry = await Dns.GetHostEntryAsync(_host);
                if (hostEntry.AddressList.Length > 0)
                {
                    ipAddress = hostEntry.AddressList[0];
                }
                else
                {
                    throw new Exception($"无法解析主机名: {_host}");
                }
            }
            var remoteEndPoint = new IPEndPoint(ipAddress, _port);
            _session = new UdpSession(_udpClient, remoteEndPoint);
            OnConnected?.Invoke(_session);

            await HandleConnectionAsync();
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"UDP连接 {_host}:{_port} 失败: {ex.Message}");
            OnDisconnected?.Invoke(_session!, ex.Message);
        }
    }

    private async Task HandleConnectionAsync()
    {
        if (_udpClient == null || _session == null)
            return;

        try
        {
            while (_isRunning)
            {
                var result = await _udpClient.ReceiveAsync();
                OnDataReceived?.Invoke(_session, result.Buffer);
            }
        }
        catch (Exception ex)
        {
            if (_isRunning)
            {
                OnDisconnected?.Invoke(_session, ex.Message);
            }
        }
    }

    public void Send(byte[] data)
    {
        _session?.Send(data);
    }

    public void Stop()
    {
        _isRunning = false;
        _udpClient?.Close();
        if (_session != null)
        {
            OnDisconnected?.Invoke(_session, "主动停止");
        }
    }
}

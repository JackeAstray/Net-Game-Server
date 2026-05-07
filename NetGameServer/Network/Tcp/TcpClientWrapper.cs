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
    private TcpClient? _tcpClient;
    private TcpSession? _session;
    private readonly string _host;
    private readonly int _port;
    private bool _isRunning;

    public event SessionConnectedHandler? OnConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnDisconnected;

    public TcpClientWrapper(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task ConnectAsync()
    {
        _isRunning = true;

        while (_isRunning)
        {
            try
            {
                Shared.Log.Info($"正在连接到 {_host}:{_port} ...");
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(_host, _port);

                _session = new TcpSession(_tcpClient);
                OnConnected?.Invoke(_session);

                await HandleConnectionAsync();
            }
            catch (Exception ex)
            {
                Shared.Log.Warning($"连接 {_host}:{_port} 失败或断开: {ex.Message}。3秒后准备重连...");
            }

            if (_isRunning)
            {
                await Task.Delay(3000); // 3秒后重连
            }
        }
    }

    private async Task HandleConnectionAsync()
    {
        if (_tcpClient == null || !_tcpClient.Connected || _session == null)
            return;

        using (_tcpClient)
        {
            var stream = _tcpClient.GetStream();
            var buffer = new byte[4096];

            try
            {
                while (_tcpClient.Connected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);

                    OnDataReceived?.Invoke(_session, data);
                }
            }
            catch (Exception ex)
            {
                OnDisconnected?.Invoke(_session, ex.Message);
                return;
            }

            OnDisconnected?.Invoke(_session, "连接关闭");
        }
    }

    public void Send(byte[] data)
    {
        _session?.Send(data);
    }

    public void Stop()
    {
        _isRunning = false;
        _session?.Close();
        _tcpClient?.Close();
    }
}

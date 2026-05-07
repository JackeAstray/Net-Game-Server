using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Network.WebSockets;

/// <summary>
/// 一个简单的 WebSocket 客户端实现。
/// </summary>
public class WebSocketClientWrapper : INetworkClient
{
    private ClientWebSocket? _webSocket;
    private WebSocketSession? _session;
    private readonly string _url;
    private bool _isRunning;

    public event SessionConnectedHandler? OnConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnDisconnected;

    public WebSocketClientWrapper(string url)
    {
        _url = url;
    }

    public async Task ConnectAsync()
    {
        _isRunning = true;

        while (_isRunning)
        {
            try
            {
                Shared.Log.Info($"正在连接到 WebSocket {_url} ...");
                _webSocket = new ClientWebSocket();
                await _webSocket.ConnectAsync(new Uri(_url), CancellationToken.None);

                _session = new WebSocketSession(_webSocket, null);
                OnConnected?.Invoke(_session);

                await HandleConnectionAsync();
            }
            catch (Exception ex)
            {
                Shared.Log.Warning($"连接 WebSocket {_url} 失败或断开: {ex.Message}。3秒后准备重连...");
            }

            if (_isRunning)
            {
                await Task.Delay(3000); // 3秒后重连
            }
        }
    }

    private async Task HandleConnectionAsync()
    {
        if (_webSocket == null || _session == null)
            return;

        var buffer = new byte[4096];

        try
        {
            while (_webSocket.State == WebSocketState.Open && _isRunning)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    break;
                }

                var data = new byte[result.Count];
                Array.Copy(buffer, data, result.Count);

                OnDataReceived?.Invoke(_session, data);
            }
        }
        catch (Exception ex)
        {
            if (_isRunning)
            {
                OnDisconnected?.Invoke(_session, ex.Message);
            }
            return;
        }

        if (_isRunning)
        {
            OnDisconnected?.Invoke(_session, "WebSocket连接关闭");
        }
    }

    public void Send(byte[] data)
    {
        _session?.Send(data);
    }

    public void Stop()
    {
        _isRunning = false;
        try
        {
            _webSocket?.Abort();
            _session?.Close();
        }
        finally
        {
            if (_session != null)
            {
                OnDisconnected?.Invoke(_session, "主动停止");
            }
        }
    }
}

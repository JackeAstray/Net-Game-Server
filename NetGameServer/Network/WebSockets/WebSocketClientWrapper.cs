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
    private ClientWebSocket? webSocket;
    private WebSocketSession? session;
    private readonly string url;
    private bool isRunning;

    public event SessionConnectedHandler? OnConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnDisconnected;

    public WebSocketClientWrapper(string url)
    {
        this.url = url;
    }

    public async Task ConnectAsync()
    {
        isRunning = true;

        while (isRunning)
        {
            try
            {
                Shared.Log.Info($"正在连接到 WebSocket {url} ...");
                webSocket = new ClientWebSocket();
                await webSocket.ConnectAsync(new Uri(url), CancellationToken.None);

                session = new WebSocketSession(webSocket, null);
                OnConnected?.Invoke(session);

                await HandleConnectionAsync();
            }
            catch (Exception ex)
            {
                Shared.Log.Warning($"连接 WebSocket {url} 失败或断开: {ex.Message}。3秒后准备重连...");
            }

            if (isRunning)
            {
                await Task.Delay(3000); // 3秒后重连
            }
        }
    }

    private async Task HandleConnectionAsync()
    {
        if (webSocket == null || session == null)
            return;

        var buffer = new byte[4096];

        try
        {
            while (webSocket.State == WebSocketState.Open && isRunning)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    break;
                }

                var data = new byte[result.Count];
                Array.Copy(buffer, data, result.Count);

                session.LastActivityTime = DateTime.UtcNow;

                OnDataReceived?.Invoke(session, data);
            }
        }
        catch (Exception ex)
        {
            if (isRunning)
            {
                OnDisconnected?.Invoke(session, ex.Message);
            }
            return;
        }

        if (isRunning)
        {
            OnDisconnected?.Invoke(session, "WebSocket连接关闭");
        }
    }

    public void Send(ReadOnlyMemory<byte> data)
    {
        session?.Send(data);
    }

    public void Stop()
    {
        isRunning = false;
        try
        {
            webSocket?.Dispose();
            session?.Close();
        }
        finally
        {
            if (session != null)
            {
                OnDisconnected?.Invoke(session, "主动停止");
            }
        }
    }
}

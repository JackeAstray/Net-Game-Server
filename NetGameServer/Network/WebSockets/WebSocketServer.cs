using System.Net;
using System.Net.WebSockets;

namespace Network.WebSockets;

/// <summary>
/// 纯净的 WebSocket 服务器，直接基于 HttpListener 而不依赖臃肿的 ASP.NET Core MVC 框架，
/// 专职负责处理游戏客户端的高频次长连接通讯。
/// </summary>
public class WebSocketServer : INetworkServer
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public event SessionConnectedHandler? OnSessionConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnSessionDisconnected;

    public Task StartAsync(int port)
    {
        _listener = new HttpListener();
        // 注意：在Windows下绑定所有IP（如+或*）可能需要管理员权限或者netsh配置，这里默认使用localhost和127.0.0.1兼容开发
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _listener != null)
            {
                var context = await _listener.GetContextAsync();
                if (context.Request.IsWebSocketRequest)
                {
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    _ = HandleWebSocketAsync(wsContext.WebSocket, context.Request.RemoteEndPoint);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
        }
        catch (HttpListenerException)
        {
            // Listener停止时可能抛出异常，忽略即可
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket Accept Loop Exception: {ex.Message}");
        }
    }

    private async Task HandleWebSocketAsync(WebSocket webSocket, EndPoint? remoteEndPoint)
    {
        var session = new WebSocketSession(webSocket, remoteEndPoint);
        OnSessionConnected?.Invoke(session);

        var buffer = new byte[4096];

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (receiveResult.Count > 0)
                {
                    var data = new byte[receiveResult.Count];
                    Array.Copy(buffer, data, receiveResult.Count);
                    OnDataReceived?.Invoke(session, data);
                }
            }
        }
        catch (Exception ex)
        {
            OnSessionDisconnected?.Invoke(session, ex.Message);
            return;
        }

        OnSessionDisconnected?.Invoke(session, "Client closed connection.");
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
        return Task.CompletedTask;
    }
}
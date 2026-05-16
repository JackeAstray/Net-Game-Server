using System.Net;
using System.Net.WebSockets;

using Network.Routing;

namespace Network.WebSockets;

/// <summary>
/// 纯净的 WebSocket 服务器，直接基于 HttpListener 而不依赖臃肿的 ASP.NET Core MVC 框架，
/// 专职负责处理游戏客户端的高频次长连接通讯。
/// </summary>
public class WebSocketServer : INetworkServer
{
    private HttpListener? listener;
    private CancellationTokenSource? cts;

    public event SessionConnectedHandler? OnSessionConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnSessionDisconnected;

    public Task StartAsync(int port)
    {
        try
        {
            listener = new HttpListener();
            // 优先绑定全部地址，满足跨机器客户端接入；若系统未授权则回退到本机地址用于本地开发。
            listener.Prefixes.Add($"http://+:{port}/");

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                Shared.Log.Warning($"[WebSocketServer] 监听所有地址失败(权限不足)，回退本机监听。详细: {ex.Message}");
                listener.Close();

                listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
            }

            cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Shared.Log.Error($"[WebSocketServer] 启动失败: {ex.Message}");
            throw;
        }

        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && listener != null)
            {
                var context = await listener.GetContextAsync();
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
            Shared.Log.Error($"WebSocket接受循环异常：{ex.Message}");
        }
    }

    private async Task HandleWebSocketAsync(WebSocket webSocket, EndPoint? remoteEndPoint)
    {
        var session = new WebSocketSession(webSocket, remoteEndPoint);
        var packetReader = new LengthPrefixedPacketReader();
        OnSessionConnected?.Invoke(session);

        var buffer = new byte[4096];

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult receiveResult;
                do
                {
                    receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (receiveResult.Count > 0)
                    {
                        packetReader.Append(buffer.AsSpan(0, receiveResult.Count));
                    }
                }
                while (!receiveResult.EndOfMessage);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

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

    public Task StopAsync()
    {
        cts?.Cancel();
        listener?.Stop();
        listener?.Close();
        listener = null;
        return Task.CompletedTask;
    }
}

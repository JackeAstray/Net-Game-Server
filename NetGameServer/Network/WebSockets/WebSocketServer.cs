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

    /// <summary>
    /// 在循环中接受传入的 HttpListenerContext，接受 WebSocket 请求并将 WebSocket 交由 HandleWebSocketAsync 处理；对于非 WebSocket 请求返回 400
    /// 并关闭响应，直到取消或 listener 为空。
    /// </summary>
    /// <remarks>在 listener 停止时可能抛出 HttpListenerException，该异常被忽略；其他异常会记录到日志。对 WebSocket 请求以非等待方式启动
    /// HandleWebSocketAsync（fire-and-forget），对非 WebSocket 请求设置 400 并关闭响应。</remarks>
    /// <param name="cancellationToken">用于在外部请求取消时终止接受循环。</param>
    /// <returns>表示接受循环的异步操作，当循环停止或发生未恢复的异常时任务完成。</returns>
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

    /// <summary>
    /// 以异步方式处理单个 WebSocket 会话，接收按长度前缀分包的数据并触发会话生命周期与数据事件。
    /// </summary>
    /// <remarks>接收循环使用固定缓冲区并通过 LengthPrefixedPacketReader 拼接与解析数据包。方法在会话创建时触发 OnSessionConnected，解析到完整数据包时触发
    /// OnDataReceived，在发生异常或连接关闭时触发 OnSessionDisconnected（异常时传递异常消息，正常关闭时传递“客户端主动关闭了连接。”）。</remarks>
    /// <param name="webSocket">用于与客户端通信的已接受 WebSocket 实例。</param>
    /// <param name="remoteEndPoint">可选的远程终结点，表示客户端来源；可能为 null。</param>
    /// <returns>表示操作完成的异步任务；在会话终止或发生异常时完成。</returns>
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

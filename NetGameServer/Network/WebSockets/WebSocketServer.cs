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
                Shared.Log.Info($"[WebSocketServer] 启动成功，监听前缀:http://+:{port}/");
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                Shared.Log.Warning($"[WebSocketServer] 监听所有地址失败(权限不足)，回退本机监听。详细: {ex.Message}");
                listener.Close();

                listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                Shared.Log.Info($"[WebSocketServer] 回退监听成功，前缀:http://localhost:{port}/, http://127.0.0.1:{port}/");
            }

            cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Shared.Log.Error($"[WebSocketServer] 启动失败 Port:{port} Exception:{ex}");
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
                    Shared.Log.Info($"[WebSocketServer] 收到握手请求 Remote:{context.Request.RemoteEndPoint} Url:{context.Request.Url}");
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    _ = HandleWebSocketAsync(wsContext.WebSocket, context.Request.RemoteEndPoint);
                }
                else
                {
                    Shared.Log.Warning($"[WebSocketServer] 拒绝非 WebSocket 请求 Remote:{context.Request.RemoteEndPoint} Url:{context.Request.Url}");
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
        Shared.Log.Info($"[WebSocketServer] 会话建立 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
        OnSessionConnected?.Invoke(session);

        var buffer = new byte[4096];
        // 单条 WS 消息字节上限（P1 DoS 修复）：攻击者用 EndOfMessage=false 无限分片会无界累积
        // packetReader 缓冲；此处按消息累计字节设上限，超限断开。
        const long MaxMessageBytes = 256 * 1024;
        long messageBytes = 0;

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult receiveResult =
                    await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (receiveResult.Count > 0)
                {
                    messageBytes += receiveResult.Count;
                    if (messageBytes > MaxMessageBytes)
                    {
                        Shared.Log.Warning($"[WebSocketServer] 单条消息超过上限 {MaxMessageBytes} 字节，断开 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                        try
                        {
                            await webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", CancellationToken.None);
                        }
                        catch { /* 尽力关闭 */ }
                        break;
                    }
                    Shared.Log.Debug($"[WebSocketServer] 接收分片 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} Count:{receiveResult.Count} EndOfMessage:{receiveResult.EndOfMessage}");
                    packetReader.Append(buffer.AsSpan(0, receiveResult.Count));
                }

                // 逐分片增量解析：完整包立即消费并派发。
                // 不再等整条消息收齐后再解析——避免缓冲随分片无界增长，也让包边界与 WS 消息边界解耦。
                while (packetReader.TryReadPacket(out var packet))
                {
                    Shared.Log.Debug($"[WebSocketServer] 完整分包 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} PacketLength:{packet.Length}");
                    OnDataReceived?.Invoke(session, packet);
                }

                if (receiveResult.EndOfMessage)
                {
                    messageBytes = 0; // 一条消息结束，计数归零
                }
            }
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"[WebSocketServer] 会话异常断开 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} Exception:{ex}");
            OnSessionDisconnected?.Invoke(session, ex.Message);
            return;
        }

        Shared.Log.Info($"[WebSocketServer] 会话正常关闭 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
        OnSessionDisconnected?.Invoke(session, "客户端主动关闭了连接。");
    }

    public Task StopAsync()
    {
        Shared.Log.Info("[WebSocketServer] 停止监听。");
        cts?.Cancel();
        listener?.Stop();
        listener?.Close();
        listener = null;
        return Task.CompletedTask;
    }
}

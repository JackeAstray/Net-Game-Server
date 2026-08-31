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

    /// <summary>
    /// 异步连接到指定的 WebSocket，并在断开或失败时按 3 秒间隔自动重试，直到停止。
    /// </summary>
    /// <remarks>连接成功后创建 WebSocketSession 并触发 OnConnected，然后调用 HandleConnectionAsync。连接失败或断开时记录警告并在 3
    /// 秒后重试。连接循环由 isRunning 控制，连接请求使用 CancellationToken.None。</remarks>
    /// <returns>表示连接及会话处理生命周期的异步操作；操作完成表示连接循环已终止。</returns>
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

    /// <summary>
    /// 处理 WebSocket 连接的接收循环，读取消息、更新会话活动时间并触发数据接收与断开事件。
    /// </summary>
    /// <remarks>当连接处于 Open 且 isRunning 为 true 时循环接收。遇到 Close 帧则以 NormalClosure 关闭并退出；接收数据时复制缓冲区到新数组、更新
    /// session.LastActivityTime 并触发 OnDataReceived。发生异常且仍在运行时通过 OnDisconnected 报告错误。方法不接受外部取消令牌。</remarks>
    /// <returns>表示异步操作的任务，完成时表示接收循环已终止。</returns>
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

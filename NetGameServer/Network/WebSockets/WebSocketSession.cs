using System.Net;
using System.Net.WebSockets;

namespace Network.WebSockets;

public class WebSocketSession : ISession
{
    private readonly WebSocket webSocket;
    private static long sessionCounter = 0;

    public WebSocketSession(WebSocket webSocket, EndPoint? remoteEndPoint)
    {
        this.webSocket = webSocket;
        RemoteEndPoint = remoteEndPoint;
        SessionId = Interlocked.Increment(ref sessionCounter);
    }

    public long SessionId { get; }

    public EndPoint? RemoteEndPoint { get; }

    public bool IsConnected => webSocket.State == WebSocketState.Open;

    public object? UserData { get; set; }

    public async void Send(byte[] data)
    {
        if (IsConnected)
        {
            try
            {
                await webSocket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            catch
            {
                // 日志记录发送异常
            }
        }
    }

    public void Close()
    {
        if (IsConnected)
        {
            webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", CancellationToken.None).Wait();
        }
    }
}
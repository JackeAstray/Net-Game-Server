using System.Buffers.Binary;
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

    public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;

    public object? UserData { get; set; }

    public async void Send(ReadOnlyMemory<byte> data)
    {
        if (IsConnected)
        {
            try
            {
                byte[] payload = EnsureLengthPrefixed(data.Span);
                await webSocket.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);
                LastActivityTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // 日志记录发送异常
                Shared.Log.Warning($"WebSocketSession Send Error: {ex.Message}");
                Close();
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

    private static byte[] EnsureLengthPrefixed(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4)
        {
            int declaredLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(0, 4));
            if (declaredLength == data.Length - 4)
            {
                return data.ToArray();
            }
        }

        byte[] framed = new byte[data.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), data.Length);
        data.CopyTo(framed.AsSpan(4));
        return framed;
    }
}

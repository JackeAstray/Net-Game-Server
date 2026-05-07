using System.Net;
using System.Net.Sockets;
using System.IO.Pipelines;
using System.Buffers;
using System.Text;

namespace Network.Tcp;

public delegate ValueTask OnMessageReceived(PipelineTcpSession session, ReadOnlySequence<byte> message);
public delegate void OnSessionClosed(PipelineTcpSession session, Exception? exception);

public class PipelineTcpServer
{
    private readonly int port;
    private Socket? listenSocket;
    private readonly CancellationTokenSource cts = new();

    public event OnMessageReceived? OnMessage;
    public event OnSessionClosed? OnClosed;

    public PipelineTcpServer(int port)
    {
        this.port = port;
    }

    public void Start()
    {
        listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listenSocket.Bind(new IPEndPoint(IPAddress.Any, port));
        listenSocket.Listen(100);

        Shared.Log.Info($"[Pipeline Server] Listening on port {port}...");
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var clientSocket = await listenSocket!.AcceptAsync(cts.Token);
                var session = new PipelineTcpSession(clientSocket);
                Shared.Log.Info($"[Pipeline Server] Accepted connection from {clientSocket.RemoteEndPoint}");

                // 处理新连接
                _ = ProcessSessionAsync(session);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Shared.Log.Error($"[Pipeline Server] Accept error: {ex}");
        }
    }

    private async Task ProcessSessionAsync(PipelineTcpSession session)
    {
        var pipe = new Pipe();
        var readingTask = FillPipeAsync(session.Socket, pipe.Writer, cts.Token);
        var processingTask = ReadPipeAsync(session, pipe.Reader, cts.Token);

        await Task.WhenAll(readingTask, processingTask);

        // 触发关闭事件
        OnClosed?.Invoke(session, null);
    }

    private async Task FillPipeAsync(Socket socket, PipeWriter writer, CancellationToken token)
    {
        const int minimumBufferSize = 512;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var memory = writer.GetMemory(minimumBufferSize);
                int bytesRead = await socket.ReceiveAsync(memory, SocketFlags.None, token);
                if (bytesRead == 0)
                {
                    break;
                }
                writer.Advance(bytesRead);

                var result = await writer.FlushAsync(token);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Shared.Log.Error($"[Pipeline Server] Read error: {ex.Message}");
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private async Task ReadPipeAsync(PipelineTcpSession session, PipeReader reader, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(token);
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                {
                    break;
                }

                // 在这里处理粘包/半包。这里假设按换行符 '\n' 分包作为示例，实际游戏多为 Length + Body
                SequencePosition? position = null;
                do
                {
                    // 示例：按 \n 分包
                    position = buffer.PositionOf((byte)'\n');
                    if (position != null)
                    {
                        var message = buffer.Slice(0, position.Value);

                        // 触发消息处理
                        if (OnMessage != null)
                        {
                            await OnMessage(session, message);
                        }

                        // 跳过处理过的消息和分隔符
                        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
                    }
                }
                while (position != null);

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Shared.Log.Error($"[Pipeline Server] Process error: {ex.Message}");
        }
        finally
        {
            await reader.CompleteAsync();
            session.Dispose();
        }
    }

    public void Stop()
    {
        cts.Cancel();
        listenSocket?.Close();
    }
}

public class PipelineTcpSession : IDisposable
{
    public Socket Socket { get; }

    public PipelineTcpSession(Socket socket)
    {
        Socket = socket;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data)
    {
        try
        {
            if (Socket.Connected)
            {
                await Socket.SendAsync(data, SocketFlags.None);
            }
        }
        catch
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (Socket.Connected)
        {
            Socket.Close();
        }
        Socket.Dispose();
    }
}
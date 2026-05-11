using System.Net;
using System.Net.Sockets;
using System.IO.Pipelines;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Network.Tcp;

public class PipelineTcpServer : INetworkServer
{
    private int port;
    private Socket? listenSocket;
    private readonly CancellationTokenSource cts = new();

    // 实现 INetworkServer 事件
    public event SessionConnectedHandler? OnSessionConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnSessionDisconnected;

    public PipelineTcpServer()
    {
    }

    public Task StartAsync(int port)
    {
        try
        {
            this.port = port;
            listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listenSocket.Bind(new IPEndPoint(IPAddress.Any, port));
            listenSocket.Listen(100);

            Shared.Log.Info($"[PipelineTcpServer] Listening on port {port}...");
            _ = AcceptLoopAsync();
        }
        catch (Exception ex)
        {
            Shared.Log.Error($"[PipelineTcpServer] 启动失败: {ex.Message}");
            throw;
        }
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var clientSocket = await listenSocket!.AcceptAsync(cts.Token);
                var session = new PipelineTcpSession(clientSocket);
                Shared.Log.Info($"[PipelineTcpServer] Session {session.SessionId} connected from {session.RemoteEndPoint}");

                OnSessionConnected?.Invoke(session);

                // 处理新连接
                _ = ProcessSessionAsync(session);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Shared.Log.Error($"[PipelineTcpServer] Accept error: {ex}");
        }
    }

    private async Task ProcessSessionAsync(PipelineTcpSession session)
    {
        var pipe = new Pipe();
        var readingTask = FillPipeAsync(session.Socket, pipe.Writer, cts.Token);
        var processingTask = ReadPipeAsync(session, pipe.Reader, cts.Token);

        await Task.WhenAll(readingTask, processingTask);

        // 触发断开事件
        OnSessionDisconnected?.Invoke(session, "Socket Closed/Error");
    }

    private async Task FillPipeAsync(Socket socket, PipeWriter writer, CancellationToken token)
    {
        const int minimumBufferSize = 1024;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var memory = writer.GetMemory(minimumBufferSize);
                int bytesRead = await socket.ReceiveAsync(memory, SocketFlags.None, token);
                if (bytesRead == 0)
                {
                    break; // Client closed connection gracefully
                }
                writer.Advance(bytesRead);
                sessionActivityMark(socket); // 模拟标记最后活动时间，可选机制

                var result = await writer.FlushAsync(token);
                if (result.IsCompleted) break;
            }
        }
        catch (Exception)
        {
            // Ignore socket generic errors silently on read failures
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private void sessionActivityMark(Socket s) { }

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

                // 拆包逻辑：4 Bytes (包体长度) + 包体内容
                while (TryReadPacket(ref buffer, out ReadOnlySequence<byte> packet))
                {
                    // 数据转化后抛给上层应用
                    // 为了统一接口，我们需要把 ReadOnlySequence 转换为 ReadOnlyMemory，若本身跨段则分配新数组
                    if (packet.IsSingleSegment)
                    {
                        OnDataReceived?.Invoke(session, packet.First);
                    }
                    else
                    {
                        OnDataReceived?.Invoke(session, packet.ToArray());
                    }
                    session.UpdateActivityTime();
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Shared.Log.Error($"[PipelineTcpServer] Process error: {ex.Message}");
        }
        finally
        {
            await reader.CompleteAsync();
            session.Dispose();
        }
    }

    /// <summary>
    /// 解析网络包：高低位 4 字节表示长度
    /// </summary>
    private bool TryReadPacket(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> packet)
    {
        packet = default;

        // 包头长度为4字节数据
        if (buffer.Length < 4) return false;

        Span<byte> lengthSpan = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(lengthSpan);

        // 小端解析长度
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(lengthSpan);

        if (buffer.Length < payloadLength + 4)
        {
            return false; // 半包状态，等待下一次数据接收
        }

        packet = buffer.Slice(4, payloadLength);
        buffer = buffer.Slice(payloadLength + 4);

        return true;
    }

    public Task StopAsync()
    {
        cts.Cancel();
        listenSocket?.Close();
        Shared.Log.Info("[PipelineTcpServer] Stopped.");
        return Task.CompletedTask;
    }
}

public class PipelineTcpSession : ISession, IDisposable
{
    private static long _sessionCounter = 0;

    public Socket Socket { get; }

    public long SessionId { get; }
    public EndPoint? RemoteEndPoint => Socket.RemoteEndPoint;
    public bool IsConnected => Socket.Connected;
    public DateTime LastActivityTime { get; private set; }
    public object? UserData { get; set; }

    public PipelineTcpSession(Socket socket)
    {
        Socket = socket;
        SessionId = Interlocked.Increment(ref _sessionCounter);
        LastActivityTime = DateTime.UtcNow;
    }

    public void UpdateActivityTime()
    {
        LastActivityTime = DateTime.UtcNow;
    }

    /// <summary>
    /// 实现 ISession 发送逻辑：为数据加上4字的长度头部
    /// </summary>
    public void Send(ReadOnlyMemory<byte> data)
    {
        if (!IsConnected) return;

        try
        {
            // 组装协议：Length(4 Bytes) + Data 
            // 在实际高并发场景，应当使用 ObjectPool<byte[]>，这里为了演示和简化
            int totalLength = data.Length + 4;
            byte[] sendBuffer = ArrayPool<byte>.Shared.Rent(totalLength);

            try
            {
                BinaryPrimitives.WriteInt32LittleEndian(sendBuffer.AsSpan(0, 4), data.Length);
                data.CopyTo(sendBuffer.AsMemory(4));

                Socket.Send(sendBuffer, 0, totalLength, SocketFlags.None);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sendBuffer);
            }
        }
        catch
        {
            Dispose();
        }
    }

    public void Close()
    {
        Dispose();
    }

    public void Dispose()
    {
        try
        {
            if (Socket.Connected)
            {
                Socket.Shutdown(SocketShutdown.Both);
                Socket.Close();
            }
        }
        catch { }
        finally
        {
            Socket.Dispose();
        }
    }
}

using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

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

            Shared.Log.Info($"[PipelineTcpServer.StartAsync] 监听端口 {port}...");
            _ = AcceptLoopAsync();
        }
        catch (Exception ex)
        {
            Shared.Log.Error($"[PipelineTcpServer.StartAsync] 启动失败: {ex.Message}");
            throw;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 异步循环监听并接受传入的 TCP 连接，建立 PipelineTcpSession，触发连接事件并将会话交给处理任务。
    /// </summary>
    /// <remarks>持续运行直到取消令牌被触发。为每个连接记录信息、触发 OnSessionConnected，并异步启动会话处理；捕获 OperationCanceledException
    /// 并记录其他异常。</remarks>
    /// <returns>表示接受循环异步执行和完成的任务。</returns>
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
            Shared.Log.Error($"[PipelineTcpServer.AcceptLoopAsync] Accept error: {ex}");
        }
    }

    /// <summary>
    /// 在独立管道上协调从套接字读取数据并交付到处理管道，直到读取或处理完成后触发会话断开事件。
    /// </summary>
    /// <remarks>方法启动 FillPipeAsync 和 ReadPipeAsync 两个并行任务，将套接字数据写入管道并从管道读取处理，随后等待两者完成并调用
    /// OnSessionDisconnected。</remarks>
    /// <param name="session">要处理的管道化 TCP 会话，包含与之关联的套接字和会话标识。</param>
    /// <returns>表示异步操作的任务；在读取与处理任务完成并触发断开事件后完成。</returns>
    private async Task ProcessSessionAsync(PipelineTcpSession session)
    {
        var pipe = new Pipe();
        var readingTask = FillPipeAsync(session.Socket, pipe.Writer, cts.Token);
        var processingTask = ReadPipeAsync(session, pipe.Reader, cts.Token);

        await Task.WhenAll(readingTask, processingTask);

        // 触发断开事件
        OnSessionDisconnected?.Invoke(session, "Socket Closed/Error");
    }

    /// <summary>
    /// 从套接字异步读取字节并写入提供的 PipeWriter，直至远端关闭、取消令牌触发或写入完成。
    /// </summary>
    /// <remarks>在循环中请求至少 1024 字节的缓冲区，调用 Socket.ReceiveAsync 填充并 Advance，随后 FlushAsync；当读取到 0 字节、FlushAsync 返回
    /// IsCompleted 或取消时停止。每次读取后可调用 sessionActivityMark 记录会话活动。发生异常时记录错误并忽略套接字通用错误；在结束时始终调用 CompleteAsync 完成
    /// writer。</remarks>
    /// <param name="socket">用于接收数据的已连接 Socket。</param>
    /// <param name="writer">用于接收并缓冲读取数据的 PipeWriter 实例。</param>
    /// <param name="token">用于取消操作的 CancellationToken。</param>
    /// <returns>表示读取并将数据刷新到管道的异步操作的完成。</returns>
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

                var result = await writer.FlushAsync(token);
                if (result.IsCompleted) break;
            }
        }
        catch (Exception ex)
        {
            // 读取失败时自动忽略套接字通用错误
            Shared.Log.Error($"[PipelineTcpServer.FillPipeAsync] Process error: {ex.Message}");
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    /// <summary>
    /// 异步从管道读取并按 4 字节长度前缀拆包，逐个将完整包交给上层回调处理，同时处理完成、异常和会话释放。
    /// </summary>
    /// <remarks>解析规则为 4 字节包体长度 + 包体内容；对跨段的 ReadOnlySequence 会复制为连续内存以传递给回调；在完成或异常时调用 reader.CompleteAsync
    /// 并释放会话，异常会被记录。</remarks>
    /// <param name="session">表示客户端会话，用于触发数据回调并更新会话活动时间。</param>
    /// <param name="reader">用于读取入站字节流的 PipeReader 实例，按长度前缀解析数据帧。</param>
    /// <param name="token">用于取消读取循环的 CancellationToken。</param>
    /// <returns>表示读取与处理循环完成的异步任务。</returns>
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
            Shared.Log.Error($"[PipelineTcpServer.ReadPipeAsync] Process error: {ex.Message}");
        }
        finally
        {
            await reader.CompleteAsync();
            session.Dispose();
        }
    }

    /// <summary>
    /// 从以4字节小端长度前缀的缓冲区中尝试读取完整数据包。
    /// </summary>
    /// <remarks>长度前缀为4字节小端整数；当缓冲区长度不足以读取长度或完整负载时视为半包并保留缓冲区不变。</remarks>
    /// <param name="buffer">按引用传入的待解析数据缓冲区；若成功读取，会将已消费的字节从缓冲区中移除。</param>
    /// <param name="packet">输出不含长度前缀的完整包数据切片；当返回 false 时为默认值。</param>
    /// <returns>若缓冲区包含完整包（4 字节长度前缀 + 负载）则返回 true 并通过 packet 输出；否则返回 false 表示需要更多数据。</returns>
    private bool TryReadPacket(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> packet)
    {
        packet = default;

        // 包头长度为4字节数据
        if (buffer.Length < 4) return false;

        Span<byte> lengthSpan = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(lengthSpan);

        // 小端解析长度
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(lengthSpan);

        if (payloadLength <= 0)
        {
            throw new InvalidDataException($"Invalid packet length: {payloadLength}");
        }
        if (payloadLength > Network.Routing.LengthPrefixedPacketReader.DefaultMaxPacketLength)
        {
            // 防 DoS：拒绝声明超大长度的包
            throw new InvalidDataException(
                $"Packet length {payloadLength} 超过最大允许 {Network.Routing.LengthPrefixedPacketReader.DefaultMaxPacketLength} 字节，已拒绝（疑似 DoS 攻击）");
        }

        if (buffer.Length < payloadLength + 4)
        {
            return false; // 半包状态，等待下一次数据接收
        }

        packet = buffer.Slice(4, payloadLength);
        buffer = buffer.Slice(payloadLength + 4);

        return true;
    }

    /// <summary>
    /// 取消内部 CancellationToken 并关闭监听套接字，停止服务器的监听和相关操作。
    /// </summary>
    /// <remarks>方法同步触发取消并关闭套接字后立即返回，不会等待后台清理或释放完成。</remarks>
    /// <returns>表示停止操作已完成的已完成 Task。</returns>
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
    /// <param name="data"></param>
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
        catch (Exception ex)
        {
            Shared.Log.Warning($"PipelineTcpSession.Send 发送异常: {ex.Message}");
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
        catch (Exception ex)
        {
            Shared.Log.Warning($"PipelineTcpSession.Dispose 处置异常: {ex.Message}");
        }
        finally
        {
            Socket.Dispose();
        }
    }
}
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Network.Udp;

/// <summary>
/// 一个简单的 UDP 客户端实现。
/// </summary>
public class UdpClientWrapper : INetworkClient
{
    private UdpClient? udpClient;
    private UdpSession? session;
    private readonly string host;
    private readonly int port;
    private bool isRunning;

    public event SessionConnectedHandler? OnConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnDisconnected;

    public UdpClientWrapper(string host, int port)
    {
        this.host = host;
        this.port = port;
    }

    /// <summary>
    /// 异步建立到指定主机和端口的 UDP 连接：解析主机名为 IP 地址、创建并连接 UdpClient、初始化 UdpSession，并在连接或断开时触发相应事件。
    /// </summary>
    /// <remarks>若主机名非直接 IP，则使用 Dns.GetHostEntryAsync 解析地址；创建并 Connect UdpClient、构造 IPEndPoint 后创建 UdpSession
    /// 并调用 HandleConnectionAsync。发生异常时记录警告并通过 OnDisconnected 报告错误。</remarks>
    /// <returns>表示异步连接操作的 Task。</returns>
    public async Task ConnectAsync()
    {
        isRunning = true;

        try
        {
            Shared.Log.Info($"正在连接到UDP {host}:{port} ...");
            udpClient = new UdpClient();
            udpClient.Connect(host, port);

            IPAddress ipAddress;
            if (!IPAddress.TryParse(host, out ipAddress!))
            {
                var hostEntry = await Dns.GetHostEntryAsync(host);
                if (hostEntry.AddressList.Length > 0)
                {
                    ipAddress = hostEntry.AddressList[0];
                }
                else
                {
                    throw new Exception($"无法解析主机名: {host}");
                }
            }
            var remoteEndPoint = new IPEndPoint(ipAddress, port);
            session = new UdpSession(udpClient, remoteEndPoint);
            OnConnected?.Invoke(session);

            await HandleConnectionAsync();
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"UDP连接 {host}:{port} 失败: {ex.Message}");
            OnDisconnected?.Invoke(session!, ex.Message);
        }
    }

    /// <summary>
    /// 异步循环接收 UDP 数据报，更新会话的最后活动时间并在接收到数据或发生异常时分别触发相应事件。
    /// </summary>
    /// <remarks>若 udpClient 或 session 为 null 会立即返回。每次成功接收后更新 session.LastActivityTime，并通过 OnDataReceived
    /// 传递接收到的字节缓冲区；在运行中发生异常且仍处于运行状态时，通过 OnDisconnected 报告异常消息。</remarks>
    /// <returns>表示操作完成的异步任务。</returns>
    private async Task HandleConnectionAsync()
    {
        if (udpClient == null || session == null)
            return;

        try
        {
            // A9 修复：与 UdpServer 保持一致的 payload 语义——每个 UDP 数据报经 LengthPrefixedPacketReader
            // 解析出完整长度帧包后派发（此前直接派发原始 datagram，含 4 字节长度前缀，链路抽象不一致）。
            var packetReader = new Routing.LengthPrefixedPacketReader();
            while (isRunning)
            {
                var result = await udpClient.ReceiveAsync();
                session.LastActivityTime = DateTime.UtcNow;
                packetReader.Append(result.Buffer);
                while (packetReader.TryReadPacket(out var packet))
                {
                    OnDataReceived?.Invoke(session, packet);
                }
            }
        }
        catch (Exception ex)
        {
            if (isRunning)
            {
                OnDisconnected?.Invoke(session, ex.Message);
            }
        }
    }

    public void Send(ReadOnlyMemory<byte> data)
    {
        session?.Send(data);
    }

    public void Stop()
    {
        isRunning = false;
        udpClient?.Close();
        if (session != null)
        {
            OnDisconnected?.Invoke(session, "主动停止");
        }
    }
}

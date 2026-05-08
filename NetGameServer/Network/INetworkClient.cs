namespace Network;

/// <summary>
/// 表示一个网络客户端的抽象接口，定义了连接、发送数据和断开连接的基本操作。
/// </summary>
public interface INetworkClient
{
    event SessionConnectedHandler? OnConnected;
    event DataReceivedHandler? OnDataReceived;
    event SessionDisconnectedHandler? OnDisconnected;

    Task ConnectAsync();
    void Send(ReadOnlyMemory<byte> data);
    void Stop();
}
namespace Network;

public interface INetworkServer
{
    /// <summary>
    /// 当有客户端连接成功时触发
    /// </summary>
    event SessionConnectedHandler? OnSessionConnected;

    /// <summary>
    /// 当接收到客户端数据时触发
    /// </summary>
    event DataReceivedHandler? OnDataReceived;

    /// <summary>
    /// 当客户端断开连接时触发
    /// </summary>
    event SessionDisconnectedHandler? OnSessionDisconnected;

    Task StartAsync(int port);
    Task StopAsync();
}
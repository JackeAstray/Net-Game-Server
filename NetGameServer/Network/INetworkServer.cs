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

    /// <summary>
    /// 开始监听指定端口，启动服务器。
    /// </summary>
    /// <param name="port"></param>
    /// <returns></returns>
    Task StartAsync(int port);
    /// <summary>
    /// 结束监听，停止服务器。
    /// </summary>
    /// <returns></returns>
    Task StopAsync();
}
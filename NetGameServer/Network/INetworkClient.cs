namespace Network;

public interface INetworkClient
{
    event SessionConnectedHandler? OnConnected;
    event DataReceivedHandler? OnDataReceived;
    event SessionDisconnectedHandler? OnDisconnected;

    Task ConnectAsync();
    void Send(byte[] data);
    void Stop();
}

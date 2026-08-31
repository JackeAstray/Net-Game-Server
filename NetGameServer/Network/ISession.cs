using System.Net;

namespace Network;

/// <summary>
/// 表示一个网络连接会话的抽象接口。
/// 不管底层是 TCP、UDP、KCP 还是 WebSockets，上层业务逻辑只关心 ISession。
/// </summary>
public interface ISession
{
    /// <summary>
    /// 会话的唯一标识
    /// </summary>
    long SessionId { get; }

    /// <summary>
    /// 客户端远程终结点信息
    /// </summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// 当前会话是否有效/处于连接状态
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 最后一次网络活动时间（用于心跳超时检测）
    /// </summary>
    DateTime LastActivityTime { get; }

    /// <summary>
    /// 发送数据
    /// </summary>
    /// <param name="data">要发送的序列化后的字节数据群</param>
    void Send(ReadOnlyMemory<byte> data);

    /// <summary>
    /// 断开连接并清理资源
    /// </summary>
    void Close();

    /// <summary>
    /// 附加在 Session 上的自定义数据（用于绑定玩家Id、账号等信息）
    /// </summary>
    object? UserData { get; set; }
}

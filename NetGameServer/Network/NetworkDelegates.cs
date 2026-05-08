using System;

namespace Network;

/// <summary>
/// 当网络事件发生时触发的委托，包含连接的 Session 和接收到的二进制数据。
/// </summary>
public delegate void DataReceivedHandler(ISession session, ReadOnlyMemory<byte> data);
public delegate void SessionConnectedHandler(ISession session);
public delegate void SessionDisconnectedHandler(ISession session, string reason);

# Network 底层网络

> 统一连接抽象（`ISession`）+ TCP/UDP/KCP/WebSocket 四种协议实现 + 零拷贝池化发送。
> 业务节点不直接用 `System.Net.Sockets`，全部走 `Network.Tcp/Udp/Kcp/WebSockets` 封装。

项目总览与能力描述见 [README.md](../../README.md) §模块详解。
本文件聚焦**代码定位、关键文件、注意事项、排错**。

## 职责边界

- ✅ 四种协议服务器/客户端封装（`TcpServer` / `TcpClientWrapper` / `UdpServer` / `KcpServer` / `WebSocketServer`）
- ✅ 统一 `ISession` 抽象（`SessionId` / `RemoteEndPoint` / `IsConnected` / `LastActivityTime` / `UserData` / `Send` / `Close`）
- ✅ 零拷贝池化发送（`PacketSender.Send` 支持 `ArrayPool` 借出缓冲区）
- ✅ 不可预测 SessionId 生成（`SessionIdGenerator`：加密随机 + 计数器混合）
- ✅ 长度帧封包（`Network.Routing.PacketBuilder`）+ 路由元数据（`RouteMetadata`）
- ❌ 不解析业务消息（业务节点做）
- ❌ 不做认证（Gateway 做）

## 关键文件

| 文件 | 职责 |
|---|---|
| `Network/ISession.cs` | 会话抽象（4 协议实现这个接口） |
| `Network/SessionExtensions.cs` | 会话扩展方法 |
| `Network/Tcp/TcpServer.cs` / `TcpSession.cs` | TCP 服务端 / 会话 |
| `Network/Tcp/TcpClientWrapper.cs` | TCP 客户端（带 OnConnected/OnDataReceived/OnDisconnected） |
| `Network/Udp/UdpServer.cs` / `UdpSession.cs` | UDP |
| `Network/Kcp/KcpServer.cs` / `KcpSession.cs` | KCP（低延迟 UDP） |
| `Network/WebSockets/WebSocketServer.cs` | WebSocket（浏览器/跨平台） |
| `Network/PacketSender.cs` | 零拷贝池化发送（`Send(ISession, byte[], int)`） |
| `Network/SessionIdGenerator.cs` | 不可预测 SessionId（`Random + Counter`） |
| `Network/Routing/PacketBuilder.cs` | 长度帧 + 路由元数据 |
| `Network/Routing/RouteMetadata.cs` | `__clientSessionId` / `__userId` / `__uid` / `__broadcast` 注入/解析 |

## 注意事项

- **零拷贝发送**：`PacketSender.Send(ISession, byte[] packet, int totalLength)` 自动选零拷贝（`TcpSession`/`TcpClientWrapper`）还是拷贝后归还（其他会话）。**调用方不要手动 `ArrayPool.Return`**——`PacketSender` 内部按是否复用 buffer 决定。
- **SessionId 不可预测**：`SessionIdGenerator.Next()` 混合 32 位随机 + 32 位计数器（高 32 位是随机基座），
  不存在顺序枚举风险。**不要**用 `Interlocked.Increment` 单独生成（可预测）。
- **路由元数据格式**：Gateway 注入 `__clientSessionId(8)` / `__userId(4)` / `__uid(?)` / `__broadcast(1)`，
  后端用 `RouteMetadata.TryExtract*` 解析。**不要**手改格式——所有节点必须一致。
- **连接关闭**：`ISession.Close()` 触发 `OnDisconnected`，Gateway/Center 等要清理会话表。
- **KCP 适用场景**：低延迟 UDP，移动网络必选。TCP 适合 WebSocket/管理面。

## 排错

| 症状 | 可能原因 | 排查 |
|---|---|---|
| 收包不完整 | 长度帧解析错（粘包/半包） | 看 `PacketBuilder.TryParseFrame` 边界处理 |
| 发送后客户端没收到 | 池化 buffer 提前归还 | 看 `PacketSender.Send` 实现，调用方不要 `ArrayPool.Return` |
| SessionId 重复 | 用了非 `SessionIdGenerator` 的实现 | 全项目统一用 `SessionIdGenerator.Next()` |
| KCP 丢包 | 拥塞窗口配置 | 看 `KcpServer` 配置（默认一般够用） |

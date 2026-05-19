# Network 网络模块

`Network` 是全项目的底层通信基础设施，负责统一连接抽象、收发处理和网络事件分发。

## 核心能力
- **多协议支持**：支持 TCP、UDP/KCP、WebSocket 等传输方式。
- **统一会话抽象**：通过 `ISession` 抽象连接标识、发送与接收行为。
- **统一事件管线**：标准化 `OnSessionConnected`、`OnDataReceived`、`OnSessionDisconnected` 生命周期。
- **服务化管理**：由 `NetworkManager` 负责多实例服务的启动、绑定和停止。

## 关键组件
- `TcpServer` / `TcpClientWrapper`：TCP 服务端与客户端封装。
- `UdpServer`（KCP/UDP 场景）：承载低延迟报文输入输出。
- `WebSocketServer`：浏览器或跨平台客户端接入支持。
- `NetworkManager`：统一管理各协议服务实例与端口监听。

## 接入建议
- 在网关中聚合多协议服务器实例，并将连接/收包/断开事件绑定到统一路由逻辑。
- 业务服务尽量只关心消息协议与处理结果，不直接耦合底层传输实现。

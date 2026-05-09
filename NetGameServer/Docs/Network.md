# Network 项目详细架构与技术文档

## 一、 项目定位
`Network` 项目为游戏提供多协议、跨服务的高性能网络中间层抽象。它主要处理传输层的连通性、字节流的处理（粘包、半包处理）、封包加密解密路由以及在分布式集群内的内部服之间/内服与客户端之间的多端连接治理。同样适用于 `.NET 10`。

## 二、 核心技术栈与依赖
为了实现高并发低延迟网络栈，该库内部封装整合了业内顶级的通讯及微服务方案：
- **Kcp**: (`Kcp` 依赖包)，基于 UDP 的可靠冗余传输，特别用于 ARPG 动作打击、即时竞技的帧同步或高频状态同步（解决 TCP 的 Head-of-line blocking 问题）。
- **Grpc & MagicOnion**: 用于服务端到服务端（S2S）的高效强类型通讯调用。MagicOnion 可以基于 gRPC 将 C# 接口变为无缝 RPC 调用。
- **NetMQ**: 包装了 ZeroMQ 的 C# 端口，作为服务间轻量的事件总线和数据队列通道使用，极大弱化复杂微服务间依赖。
- **Yarp.ReverseProxy**: 微软的高性能跨平台反向代理中间件引擎，能够处理庞大对外的 TCP/HTTP/WS 连接，智能承接负载路由（如网关集群代理到后面的具体网游服）。
- **Polly**: 作为网络容错防护墙。对于不稳定的发包、服务超时等提供熔断、超时与平滑重试规则。
- **Microsoft.IO.RecyclableMemoryStream**: 内存池化的核心组件。将大量的 Socket `byte[]` 字节数组申请交给池管理，以最大化削减 GC 回收抖动，在游戏环境中极为关键。

## 三、 详细模块拆解

### 1. 通讯协议底座 (`Network/Tcp/`, `Network/Udp/`, `Network/WebSockets/`, `Network/Http/`)
按支持网络协议种类切割：
- **Tcp**: 
  - `TcpServer` 与 `TcpSession` 使用 `System.Net.Sockets.SocketEventArgs` 进行基础异步高并发处理（IOCP/Epoll 模型）。
  - `PipelineTcpServer`: 更进一步采用现代 .NET System.IO.Pipelines 技术减少内存数据拷贝实现极速的基于 TCP 数据收发。
- **Udp**: 
  - `UdpServer` & `UdpSession`: 与 Kcp 内部结合应用，管理非连接环境下的源地址和报文传输安全机制。
- **WebSockets**:
  - `WebSocketServer/Session`: 使用 HttpListener 升级到 Ws。方便进行 WebGL （网页H5小游戏/微信小游戏）端跨平台玩家接入联机交互。

### 2. 接口抽象层 (`Network/INetworkX.cs`)
提供给外部的统一控制门面：
- `INetworkServer` / `INetworkClient` / `ISession`: 你无需了解底层是 TCP 还是 WS，都可以获得通用的 `Start()`, `Send(byte[] buffer)`, 并在回调 `NetworkDelegates` 中一致地获取下发的连接或收到字节数据事件。

### 3. 数据处理管线 (`Network/Routing/`)
处理最复杂的“流变消息”逻辑：
- `PacketBuilder.cs`: 进行网络应用层封包（封包头：如 包总长度(2字节) + CommandID/消息类型(2字节) + Protobuf/MemoryPack流 ）。
- `MessageRouter.cs`: **解包分发器**。收到组装完整独立的包数据后，读取头部的 Message ID，然后查表使用泛型机制抛射转发给对应业务代码的真实方法（Handler Delegate）。
- `NetworkManager.cs`: 上下文管理器，维持已接纳所有在线用户、跨服连接的上下文，定时进行超时剔除（心跳保活检测机制）。

## 四、 开发规范
1. **统一流处理池机制**：严禁随意 `new byte[1024]`，所有的数据读取组装必须获取 `RecyclableMemoryStream` 中持有的跨生命周期缓冲区。
2. **多线程安全**：`MessageRouter` 推送给主游戏层的消息大多是来自于底层的异步 IO 线程，如果业务涉及主线程的写入务必抛向主游戏循环（Main Thread Tick），不得直接访问共享的数据结构。

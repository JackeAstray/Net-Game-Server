# Net-Game-Server
基于 `.NET 10` 的分布式微服务游戏服务器集群框架。

这是一个高性能、可扩展的分布式游戏服务端解决方案。支持 TCP、UDP、KCP 和 WebSockets 等多种网络协议，适用于开发从卡牌、MMO 到实时竞技类等多种类型的在线游戏应用。

## 核心特性
- **分布式微服务架构**: 服务器根据业务拆分为多个独立节点，降低耦合，易于水平扩展。
- **高性能网络库**: 自定义 `Network` 模块支持 TCP/UDP/KCP/WS 协议，适合不同的网络环境。
- **自定义序列化**: 优化包体大小，采用 `Shared.Json` 或二进制序列化提升性能。
- **统一网关调度**: `Gateway` 节点处理异构客户端连接，将长连接和短连接动态路由到内部集群。
- **服务发现与状态同步**: 通过 `Center` 服务器进行集群节点管理和注册。

## 项目结构
- **[Gateway](NetGameServer/Docs/Gateway.md)**: 网关服务器，统一处理客户端的长短期连接。
- **[Login](NetGameServer/Docs/Login.md)**: 登录服务器，处理账号系统、登录验证相关业务，支持 HTTP 与 Socket 协议。
- **[Center](NetGameServer/Docs/Center.md)**: 全局控制中心服务器，管理服务节点，执行跨服匹配及调度。
- **[Game](NetGameServer/Docs/Game.md)**: 通用游戏逻辑服务器（背包、公会、社交、任务等）。
- **[Battle](NetGameServer/Docs/Battle.md)**: 战斗物理场景服务器，负责核心对局判定和场景状态同步。
- **[DB](NetGameServer/Docs/DB.md)**: 数据库服务数据持久化节点，支持对数据库和缓存代理（Redis/MySQL等）的访问。
- **[Network](NetGameServer/Docs/Network.md)**: 底层网络通信模块，承载 TCP/UDP/KCP/WS 网络连接和传输能力。
- **[Shared](NetGameServer/Docs/Shared.md)**: 共享模块库，提供公用实体、统一常量、配置解析、统一序列化辅助等。

## 环境要求
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 可选 (具体基于业务逻辑扩展)：
  - MySQL / SQL Server
  - Redis

## 快速开始

### 1. 编译项目
使用 Visual Studio 或基于控制台的 `dotnet build` 编译整个解决方案：
```bash
dotnet build NetGameServer.sln
```

### 2. 节点启动顺序
为了让各服务节点能正常注册与通信，启动应该遵循以下顺序：
1. **DB Server** (数据层)
2. **Center Server** (中心路由和状态控制)
3. **Login Server** (账号及认证服务)
4. **Game / Battle Server** (业务逻辑层)
5. **Gateway Server** (网关层，最后启动以接受外部客户端连接)

你可以直接去各自目录执行 `dotnet run`，或者通过脚本统一启动。

## 架构说明
客户端与服务端通信，所有的外部流量统一打到 `Gateway`，然后由 `Gateway` 将报文解析成 `[SessionId(8)][MsgId(4)][Payload]` 形式，再投递向后方（如 `Login`，`Game` 等）。内部服务器处理完毕后，再通过同一连接投递回应，`Gateway` 根据 `SessionId` 把回应发给指定的客户端应用。

## 贡献
欢迎提交 Issue 和 Pull Request，我们共同打造更好的基础服务端框架。

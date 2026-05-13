# Net-Game-Server
游戏服务器  Game Server

此项目展示了一个典型的基于 `.NET` 的分布式微服务游戏服务器集群的实现。

## 项目结构
- **[Gateway](NetGameServer/Docs/Gateway.md)**: 网关服务器，统一处理客户端的长短期连接。
- **[Login](NetGameServer/Docs/Login.md)**: 登录服务器，处理账号系统、登录验证相关业务。
- **[Center](NetGameServer/Docs/Center.md)**: 全局控制中心服务器，管理服务节点，执行跨服匹配及调度。
- **[Game](NetGameServer/Docs/Game.md)**: 通用游戏逻辑服务器（背包、公会、社交、任务等）。
- **[Battle](NetGameServer/Docs/Battle.md)**: 战斗物理场景服务器，负责核心对局判定和场景状态同步。
- **[DB](NetGameServer/Docs/DB.md)**: 数据库服务/缓存代理。
- **[Network](NetGameServer/Docs/Network.md)**: 底层网络通信模块，承载TCP/UDP/KCP/WS连接能力。
- **[Shared](NetGameServer/Docs/Shared.md)**: 共享模块库（常量、公用实体、统一辅助工具等）。

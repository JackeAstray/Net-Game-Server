# App 应用节点

> "游戏服务器 + 应用服务器"并存的承载节点（最小骨架，2026-09-14 新增）。
> 与游戏节点共享全部基建：内部 TCP 认证（HMAC fail-closed）、Center 注册 + 心跳、
> 新协议强类型分发（`AppDispatcher`）、优雅关闭、健康探针；
> 额外承载 ASP.NET Core REST API（应用服务器 HTTP 面）。
>
> 项目总览见 [README.md](../../README.md)，编码规范见 [Code-Style.md](Code-Style.md)。

## 职责边界

- ✅ 接收 Gateway 转发的客户端消息（ID 段 80000-89999，`[GameMessage] Target="App"`）
- ✅ 提供 HTTP REST 应用面（控制器 + API Key 鉴权，`AppHttpPort`）
- ✅ 注册 Center（`NodeType="App"`）+ 周期心跳，与游戏节点在管理台同列
- ❌ 不做战斗判定 / 场景逻辑（Battle 节点）
- ❌ 不做账号登录（Login 节点）、持久化（DB 节点）
- ❌ 不做客户端流量接入（Gateway 节点）

## 入口与启动

- 启动入口：`App/Program.cs`（顶级语句）
- 内部 TCP 端口默认 `31308`（`AppPort`）；HTTP 应用面端口默认 `31309`（`AppHttpPort`）
- 健康检查端口默认 `31308 + 10000 = 41308`（`HealthPort`）
- 启动依赖：Center（注册 + 心跳）；Gateway（可选，路由 80000-89999 段消息到本节点）
- 启动脚本：`Publish/StartServers.bat` 已含 App；Docker：`deploy/docker-compose.yml` 的 `app` 服务

## 关键文件

| 文件 | 职责 |
|---|---|
| `App/Program.cs` | 启动入口（日志 / RemoteLog / HealthServer / NodeLifecycle） |
| `App/AppServerApp.cs` | 节点主类（partial）：内部 TCP + 认证 + 分发、Center 注册/心跳、HTTP 启动、优雅关闭 |
| `App/Handlers/AppDispatcher.cs` | 强类型消息分发（`AppSessionContext` + `BuildDispatcher`） |
| `App/Controllers/AppController.cs` | HTTP REST 示例（info / ping / echo） |
| `App/Auth/AppApiKeyMiddleware.cs` | 应用面 API Key 鉴权（fail-closed，恒定时间比较） |
| `Framework/Framework.Protocol/Messages/AppMessages.cs` | 协议消息 `AppEchoRequest(80001)` / `AppEchoResult(80002)` |
| `App/appsettings.json` | 节点配置（Host / Port / HttpPort） |

## 消息协议（最小示例）

客户端经 Gateway 发送 `AppEchoRequest`（80001，`Target="App"`，MemoryPack）→
Gateway 按 `RouterTable` 路由到 App 节点 → `AppDispatcher` 处理后回包 `AppEchoResult`（80002）→
Gateway 出站白名单放行（`IsClientVisibleOutboundMsgId` 含 80000-89999）→ 客户端。

新增应用业务消息：在 `AppMessages.cs` 声明 `[GameMessage(id, Target = "App", Reply = "...")]`，
`AppDispatcher.BuildDispatcher` 注册处理器，然后重生成客户端产物：

```bash
dotnet run --project NetGameServer/Tools/ClientGen -c Release -- NetGameServer/Protocol/defs NetGameServer/Tools/ClientGen/Output
```

## HTTP 应用面

- 公开端点（匿名）：`GET /api/app/info`、`GET /api/app/ping`、`/health`
- 受 Key 保护：其余端点需请求头 `X-Api-Key`（配置键 `HttpApiKeys`，逗号/换行分隔；未配置时 fail-closed 全部拒绝）
- 安全默认：`AppHttpListenAddress` 默认 `127.0.0.1`（仅回环）；生产放开监听后须在反代/LB 层终止 TLS
  （本节点明文 HTTP，与 Login API 的 `ForceApiHttps` 模式对齐的 HTTPS 强制作后续增强）

## 注意事项

- **内部认证与游戏节点一致**：`SecretConfig.Require("CenterNodeSharedSecret")` fail-closed（拒绝占位符）；
  网关连接必须完成 `InternalAuthFilter` 握手，未认证连接拒绝业务消息。
- **端口段约定**：80000-89999 为 App 段（70000-70999 网关传输层、90000+ 内部、其余归各游戏节点）。
- **负载上报**：最小骨架固定返回 1；后续可接队列深度 / 在线会话数。
- **无 DB/Redis 直连**：应用业务如需持久化走 DB 节点（`DbDispatcher`）或独立存储服务。

## 排错

| 症状 | 可能原因 | 排查 |
|---|---|---|
| 客户端发 80001 无响应 | Gateway 未连接 App / 路由未命中 | 看 Gateway 日志 `已连接到 App` / `未知的路由目标` |
| App 注册不上 Center | Center 未启动 / 内部认证失败 | 看 App 日志 `已连接到 Center`；看 Center 日志 `InternalAuth` 拒绝；`netgame_center_nodes` 计数 |
| HTTP 401 | 未配置 `HttpApiKeys` 或 Key 错 | 检查环境变量 / `appsettings.Local.json`；匿名路径见 `AppApiKeyOptions.AllowAnonymousPaths` |
| 出站 80002 被网关丢弃 | msgid 不在出站白名单区间 | 确认 `IsClientVisibleOutboundMsgId` 含 80000-89999 |

# Gateway 网关节点

> 外部客户端连接集群的统一入口，承接 TCP/UDP/KCP/WebSocket 四种协议，
> 按 `Protocol/defs` 生成的路由表配置化转发到后端节点，并在入口强制会话时间窗。
>
> 项目总览与节点拓扑见 [README.md](../../README.md)，协议约束见 [Protocol.md](Protocol.md)。

## 职责边界

- ✅ 客户端连接接入 + 心跳 + 断开清理
- ✅ 协议解析（长度帧 + MsgId）
- ✅ 按 `RouterTable` 把客户端消息路由到 Login/Game/Center/Battle
- ✅ 注入路由元数据（`__clientSessionId` / `__userId` / `__uid` / `__nickname` / `__broadcast` / `__targetSessionId` / `__requestId`）
- ✅ 登录成功后把 `clientSessionId` 切换到新 Gateway 连接（断线重连）
- ✅ 会话时间窗强制（`SessionGuard.IsSessionValid`，迭代 16）
- ✅ 入站防护：每会话速率限制（`GatewayMaxInboundPerSecond` 默认 600/s）+ 入站/转发帧上限（4+64KB）+ `StripClientFields` 剥离客户端伪造的 `__*` 元数据（P0）
- ✅ 出站白名单：`IsClientVisibleOutboundMsgId` 拒绝后端伪造内部/越界消息回客户端
- ✅ 顶号：`BindUser` 检测同 userId 旧会话 → `KickOutOldSession` 关闭旧连接
- ✅ HTTP 反向代理（YARP）：`/api`、`/swagger` 转发到 Login（`GatewayHttpPort` 默认 31301）
- ❌ 不做业务校验、不存游戏状态（业务下沉到 Game/Battle）
- ❌ 不接 `internal="true"` 的内部消息（已拒绝）

## 入口与启动

- 启动入口：`Gateway/Program.cs`（顶级语句，加载配置 + `GatewayServerApp.StartNetworkAsync`）
- 监听端口默认 `31300`（配置 `GatewayPort`）
- 启动顺序：DB → Center → Login → Game/Battle → **Gateway 最后**（接受外部流量）

## 关键文件

| 文件 | 职责 |
|---|---|
| `Gateway/Program.cs` | 启动入口（顶级语句） |
| `Gateway/GatewayServerApp.cs` | 节点主类（partial） |
| `Gateway/GatewayServerApp.Network.cs` | 四种协议服务器 + 客户端收包入口（`onDataReceived`，含 `SessionGuard` 校验） |
| `Gateway/GatewayServerApp.Backend.cs` | 与 Login/Game/Center 后端 TCP 客户端 |
| `Gateway/GatewayServerApp.CenterClient.cs` | Center 客户端（注册/心跳/迁移通知） |
| `Gateway/GatewayServerApp.Sessions.cs` | 断线 / 重连处理 + HTTP 反代（YARP） |
| `Gateway/Managers/GatewaySessionManager.cs` | 会话表（`clientSessionId → ISession`、`CreatedAt`、userId/uid/nickname 绑定、`uidSessions` 反向索引、`bindGate` 互斥） |

## 注意事项

- **路由表优先**：`onDataReceived` 先查 `RouterTable.GetTargetServer(msgId)`，未匹配再回退到旧区间路由
  （10000~20000 → Login 等）；新增消息必须改 def 而非手改区间。
- **会话时间窗**：所有客户端入包都过 `SessionGuard.IsSessionValid`，超 `MaxSessionLifetime`（2h）
  直接 `session.Close()` + 丢弃，不要注释掉这行。
- **内部消息拒绝**：`route.IsInternal == true` 的消息（如 Center 内部 90001~90010 / 90999 / 91001~91010，DB 1000~1127，Login 10000/10014）直接拒绝并 Log.Warn，
  防止客户端伪造内部协议。
- **入站限流/帧上限**：每会话 `GatewayMaxInboundPerSecond`（默认 600/s）超限断开；入站帧与转发帧均设长度上限，
  防恶意连接打爆内存。`StripClientFields` 在入站剥离客户端伪造的 `__*` 路由元数据（防越权指定目标会话/广播）。
- **出站白名单**：后端回包只有 `IsClientVisibleOutboundMsgId` 允许的 MsgId 才会转发给客户端，
  内部消息（`internal=true`）与未登记 MsgId 一律拒绝，防后端节点被攻破后伪造客户端消息。
- **顶号**：同一 userId 再次 `BindUser` 时旧会话被 `KickOutOldSession` 关闭并清绑定（`bindGate` 互斥保证双向表一致）。
- **断线重连（KBE 式）**：客户端断开时若有 userId 绑定，进入挂起队列，宽限期内重新登录可
  把新会话迁移到旧 ID（后端按旧 ID 续接实体）。
- **跨实例重连（B1）**：挂起会话上报 Center 目录（90012/90013），换 Gateway 重连时按 UserId
  查询（90014/90015）拿到旧 `clientSessionId`，在新实例恢复别名并通知 Battle，实现多 Gateway 粘性会话。

## 排错

| 症状 | 可能原因 | 排查 |
|---|---|---|
| 客户端连上但消息无响应 | 路由目标节点未启动 / 未注册 | 看 `GatewayServerApp.Backend.cs` 的 `OnConnected` 回调是否触发 |
| 客户端连上后被立刻断开 | `SessionGuard.IsSessionValid` 拒绝（`CreatedAt` 未记录 / 时间漂移） | 看 Gateway 日志 `超过最大生命周期`，检查 `AddSession` 是否被调到 |
| `internal` 消息泄露 | 客户端伪造内部消息 | 看日志 `拒绝客户端发送的内部消息` |
| 广播没发出去 | `RouteMetadata` 没附 `__broadcast` | 看后端发包路径（`Center` 的房间广播走此通道） |
| 端口占用 | 上一个 Gateway 没关 | `netstat -ano \| findstr 31300` 找残留进程 |

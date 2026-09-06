# Login 登录节点

> 账号体系：注册、登录、Token 签发、登录限流。
> 通过 TCP 与 DB 节点交互（DB 链路 `[MsgId(4)][Payload(尾部附 __requestId 元数据)]`，见 [Protocol.md](Protocol.md)）。
>
> 项目总览见 [README.md](../../README.md)，编码规范见 [Code-Style.md](Code-Style.md)。

## 职责边界

- ✅ HTTP / Socket 双协议接入（HTTP 用于无状态接口如注册/查询；Socket 经 Gateway 转发）
- ✅ 账号密码校验（凭据走 DB 节点查表）
- ✅ HMAC-SHA256 Token 签发（含 `SessionSeq=1`，迭代 16 起单调序号防重放）
- ✅ 登录限流（按账号维度，失败计数 + 冷却）
- ❌ 不做角色/背包等业务（业务下沉到 Game 节点）
- ❌ 不验证 `SessionSeq` 单调性（Verify 是 TokenService 的能力，Login 只签发不消费）

## 入口与启动

- 启动入口：`Login/Program.cs`（顶级语句）
- 监听端口默认 `31302`（`LoginPort`）
- 启动依赖：**DB 必须先启动**（登录要查账号表）

## 关键文件

| 文件 | 职责 |
|---|---|
| `Login/Program.cs` | 启动入口 |
| `Login/LoginServerApp.cs` | 节点主类（partial） |
| `Login/Handlers/LoginHandler.cs` | 登录业务：`HandleLoginRequestAsync` + `IssueToken(userId, uid)` / `VerifyToken(token)` |
| `Login/Managers/` | SessionManager 等 |

## 注意事项

- **Token 密钥**：`TokenSecret` 从配置读取（缺省时用随机 GUID——重启后旧 Token 全部失效，保证安全）。
  生产环境**必须**显式配置固定密钥，否则重启会强制所有用户重登。
- **限流维度**：双轨——账号维度本地计数（`TryGetThrottleRemaining`）+ Redis 集中计数
  （`throttle:{action}:{identity}:fail/lock`，多实例共享；Redis 不可用时自动回退本地，fail-open）。
- **限流 vs Token 校验**：`HandleLoginRequestAsync` 是登录链路（计限流），
  `VerifyToken` 是后续业务校验（不计限流），不要混用。
- **登录成功不直发 Token 字段名修改**：客户端按 `LoginResponse.Token` 取，字段重命名要同步客户端。

## 排错

| 症状 | 可能原因 | 排查 |
|---|---|---|
| 登录返回 `账号不能为空` | `request.Account` trim 后空 | 看 `LoginHandler.HandleLoginRequestAsync` 入参校验 |
| 登录返回失败但密码正确 | DB 节点未启动 / 链路断 | 看 `dbClient.OnConnected` 是否触发；看 `Network/Routing` 链路 |
| Token 立即失效 | `TokenSecret` 用了随机密钥且重启了 | 显式配置 `TokenSecret` 持久化 |
| 限流触发但用户没暴力破解 | 限流计数未清（重启进程会清） | 多实例部署需集中计数（Redis） |

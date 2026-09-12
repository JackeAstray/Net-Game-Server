# Login 登录节点

> 账号体系：注册、登录、Token 签发、登录限流。
> 通过 TCP 与 DB 节点交互（DB 链路 `[MsgId(4)][Payload(尾部附 __requestId 元数据)]`，见 [Protocol.md](Protocol.md)）。
>
> 项目总览见 [README.md](../../README.md)，编码规范见 [Code-Style.md](Code-Style.md)。

## 职责边界

- ✅ HTTP / Socket 双协议接入（HTTP 用于无状态接口如注册/查询；Socket 经 Gateway 转发）
- ✅ 账号密码校验（凭据走 DB 节点查表）
- ✅ HMAC-SHA256 Token 签发（`SessionSeq` 经 `AntiReplayState.IssueNextSeq` 单调递增，同用户新登录不互相覆盖）
- ✅ Token 防重放消费：`VerifyToken` 接入 `AntiReplayState.TryAcceptSeq`，旧 Token/重放 seq 被拒（迭代 16 起）
- ✅ 登录限流（按账号维度，失败计数 + 冷却，本地 + Redis 双轨）
- ✅ HTTP 管理 API（`AccountController`，端口 `ApiPort` 默认 31303，`ApiKeyAuthMiddleware` 鉴权 + 登录 Token 绑定本人账户）
- ✅ 找回密码两阶段：发送验证码（阶段一登记）+ 验证码重置（阶段二校验后落库，`ResetPasswordWithCodeReq/Res`=10016/10017）
- ❌ 不做角色/背包等业务（业务下沉到 Game 节点）

## 入口与启动

- 启动入口：`Login/Program.cs`（顶级语句）
- 监听端口默认 `31302`（`LoginPort`）
- 启动依赖：**DB 必须先启动**（登录要查账号表）

## 关键文件

| 文件 | 职责 |
|---|---|
| `Login/Program.cs` | 启动入口 |
| `Login/LoginServerApp.cs` | 节点主类（partial）+ TCP 收包 + HTTP WebApi（`StartWebApiAsync`） |
| `Login/Handlers/LoginHandler.cs` / `LoginHandler.Account.cs` / `LoginHandler.Security.cs` | 登录业务 partial 拆分：登录/注册/找回密码（Account）、Token 签发/验证/限流/DB 封装（Security） |
| `Login/Handlers/MessageRouter.cs` | 强类型分发器（新协议优先）+ 旧路由回退 |
| `Login/Controllers/AccountController.cs` | HTTP API（login/register/find-password/query-account/online-stats） |
| `Login/ApiKeyAuthMiddleware.cs` | HTTP API Key 鉴权（`X-Api-Key` 头 + 恒定时间比较） |
| `Login/Managers/` | SessionManager 等 |

## 注意事项

- **Token 密钥**：`TokenSecret` 从配置读取（缺省时用随机 GUID——重启后旧 Token 全部失效，保证安全）。
  生产环境**必须**显式配置固定密钥，否则重启会强制所有用户重登。
- **限流维度**：双轨——账号维度本地计数（`TryGetThrottleRemaining`）+ Redis 集中计数
  （`throttle:{BuildActionKey(action,identity)}:{fail|lock}`，多实例共享；Redis 不可用时自动回退本地，fail-open）。
- **找回密码（两阶段）**：阶段一 `find-password` 仅登记一次性验证码（本地 `pendingPasswordResets` + Redis `reset_code:{account}` 双写，TTL 10min），
  统一成功提示防账号/邮箱枚举；阶段二 `reset-password-with-code` 校验 CodeHash（恒定时间比较）后调 DB 落库，成功后双删。
- **限流 vs Token 校验**：`HandleLoginRequestAsync` 是登录链路（计限流），
  `VerifyToken` 是后续业务校验（不计限流，但消费防重放 seq），不要混用。
- **登录成功不直发 Token 字段名修改**：客户端按 `LoginResponse.Token` 取，字段重命名要同步客户端。

## 排错

| 症状 | 可能原因 | 排查 |
|---|---|---|
| 登录返回 `账号不能为空` | `request.Account` trim 后空 | 看 `LoginHandler.HandleLoginRequestAsync` 入参校验 |
| 登录返回失败但密码正确 | DB 节点未启动 / 链路断 | 看 `dbClient.OnConnected` 是否触发；看 `Network/Routing` 链路 |
| Token 立即失效 | `TokenSecret` 用了随机密钥且重启了 | 显式配置 `TokenSecret` 持久化 |
| 限流触发但用户没暴力破解 | 限流计数未清（重启进程会清） | 多实例部署需集中计数（Redis） |

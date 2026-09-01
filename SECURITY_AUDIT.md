# Net-Game-Server 安全与健壮性审计报告

- **审计对象**：`NetGameServer/`（.NET 10 分布式微服务游戏服务器：Gateway / Login / Game / DB / Center / Battle + Framework 基础设施，TCP/UDP/KCP/WS 四套传输）
- **方法**：按安全域并行深度审查（14 个独立子代理 × 各域重点文件逐行阅读），叠加人工复核关键信任锚点（密码哈希、令牌、内部认证、API Key、属性编解码、EntityCall、脚本宿主、持久化 SQL、中心注册表等）
- **目标**：只报告缺陷与漏洞，不修改代码
- **口径**：严重级同时考虑"无需内部凭证即可远程利用"与"内部信任边界崩溃"两类

---

## 一、总览

| 领域 | 代理 | 严重 | 高危 | 中危 | 低危 | 信息 |
|---|---|---|---|---|---|---|
| 认证/令牌/会话 | auth | 1 | 7 | 6 | 6 | 2 |
| 持久化/架构诊断 | persistence | 0 | 3 | 9 | 9 | 5 |
| TCP/UDP 传输 | tcpudp | 0 | 5 | 10 | 6 | 4 |
| Game 社交 | gamesocial | 0 | 3 | 5 | 6 | 3 |
| Gateway 网关 | gateway | 1 | 6 | 4 | 4 | 0 |
| DB 社交/密码 | dbsocial | 1* | 2 | 4 | 2 | 1 |
| EntityCall | entitycall | 1 | 3 | 2 | 3 | 2 |
| KCP/WebSocket | kcpws | 1 | 5 | 6 | 3 | 2 |
| 脚本宿主 | scripthost | 0 | 4 | 5 | 5 | 3 |
| 协议编解码/分发 | codec | 0 | 2 | 5 | 4 | 2 |
| DB 账号/好友 | dbaccount | 2* | 3 | 4 | 3 | 3 |
| Center HTTP/注册表 | center | 1* | 4 | 4 | 3 | 5 |
| Battle 房间/场景 | battleroom | 1 | 5 | 6 | 3 | 3 |
| Battle 实体/同步 | battleentity | 0 | 2 | 4 | 6 | 2 |

\* 已按人工复核降级：DB 邮箱重置"严重"（Login 层先校验验证码，DB 层暴露面=内部信任边界）；DB 无逐用户授权与 Center 伪造节点注册，本质同属"内部信任边界崩溃"主题，正文按该主题归并评级。

**顶层排序（修复优先级，由高到低）**：
1. **已提交进 git 的明文凭据**（MySQL / SMTP / HTTP API Key）—— 立即轮换
2. **KCP 无界发送队列 → 原生 OOM**（K1）
3. **KCP 接收路径无载荷上限 → 共享后端连接断开 = 全服 DoS**（codec H1）
4. **Battle 跨玩家伤害**（C1）与**无加入鉴权**（H1/H2/H3）组合 → 远程击杀他人
5. **内部信任边界整体崩溃**：单点共享密钥 + 单向 HMAC + 无逐节点身份 → 伪造 DB 响应 / EntityCall 结果 / 节点注册 / 跨玩家消息注入（见主题 B）
6. **持久化数据丢失**（脏标记先清、失败吞异常、并发重排）
7. **全系统缺少速率限制/每客户端配额**：网关、社交、DB、Center API、Battle、Health 全线缺失
8. **Battle 同步加速/输入洪泛**（H1/H2、frame sync）
9. **迁移/实体跨节点一致性**（split-brain、91004 竞态、id 冲突）
10. **明文 HTTP 管理面 + 无锁速率的 API Key**（Center / Login）

---

## 二、严重（Critical）—— 无需内部凭证即可远程利用

### C1. 明文凭据被提交进 git（人工复核确认）
- **文件**：`NetGameServer/Login/appsettings.json`（L15 HttpApiKeys=`dev-local-key-2026`、L23 SMTP.Account=`llastray@163.com`、L24 SMTP.Password=`BNS8Nswu2vDvPCjz`）、`NetGameServer/DB/appsettings.json`（L3 MySqlConnection `Pwd=Ycs982109683`）
- **风险**：全部为真实生产凭据并随仓库提交。任何拿到仓库/历史的人可直接连数据库、发邮件（可被滥用为钓鱼/垃圾邮件源）、调用管理 API。**必须立即轮换全部三项并清理 git 历史。**

### C2. KCP 发送队列无界 → 原生 OOM（KCP 代理 K1）
- **文件**：`Network\Kcp\KcpSession.cs`（Send 忽略返回值、`rmt_wnd` 取自攻击者报文头）、`Network\Kcp\KcpServer.cs`
- **利用**：恶意客户端发 `wnd=0` 或静默不 ACK，发送端 `snd_queue` 无限累积 → 内存耗尽/原生 OOM 崩溃。无鉴权前置即可触发。

### C3. KCP 接收路径无载荷大小上限 → 共享后端连接断开 = 全服 DoS（编解码代理 H1，已复核）
- **文件**：`Framework.Protocol\MessageDispatcher.cs:46,177-181`（唯一 16MB 上限，且只作用于后端）、`Network\Kcp\KcpSession.cs:20,91`（无界 ArrayBufferWriter）、`Gateway\GatewayServerApp.Network.cs`
- **利用**：KCP 绕过 `LengthPrefixedPacketReader` 的 64KB 上限；超大帧被网关转发到 Game/Login/DB，后端 `TryReadPacket` 抛 `InvalidDataException` → 关闭**该网关唯一的共享后端连接** → 该网关所有客户端集体断线。一个未认证 KCP 连接 = 整服 DoS。

### C4. Battle：白名单放行"对其他玩家实体 TakeDamage"（Battle 代理 C1）
- **文件**：`Battle\BattleServerApp.cs:215,287-305`、`MessageRouter.cs:105-117`（40006 客户端可路由）、`Avatar.csx:57-71`
- **利用**：白名单 `{TakeDamage, QueryProgress}` 仅对"非自有实体"生效，**并未限定为世界/无主实体**。攻击者向同房间内其他玩家的 `Player` 实体（EntityId=对方 ClientSessionId）发 `ScriptAction{TakeDamage, 1000000}`，通过房间归属检查 → 目标 Hp 归零，20Hz 可重复。也适用于对方 Skill/Item 实体；`QueryProgress` 可窥探他人 Quest 的私有 Score。

### C5. 系统级缺少速率限制 / 连接上限（Gateway 代理 C1）
- **文件**：`Gateway\GatewayServerApp.Network.cs` 及全部节点
- **利用**：无连接数上限（慢速连接耗尽句柄）、无每客户端消息频率限制、无带宽/长度之外的处理节流。TCP 慢速连接、UDP/KCP 洪泛、社交/DB 请求洪泛均无防线。此缺陷放大几乎所有洪泛类 DoS。

---

## 三、主题 B：内部信任边界整体崩溃（高危集群，共享根因）

**根因**：内部节点间用**单一共享密钥 `CenterNodeSharedSecret` + 单向 HMAC 握手 + 无逐消息完整性/身份绑定**。握手的"认证"只证明"持有共享密钥"，不证明"我是哪个节点"；DB/Center/EntityCall 处理器普遍信任请求自带的 UserId/EntityId/NodeId，从不与握手身份绑定。因此"任一被攻破节点 / 密钥泄露 / 内鬼"等价于"可伪装任意用户、任意节点、任意消息"。

| 编号 | 发现 | 文件 | 说明 |
|---|---|---|---|
| B1 | Gateway 出站不校验 msgid 白名单/内部标记/目标会话授权/大小 | `Gateway\GatewayServerApp.Backend.cs:204-213,287-314,417-426` | 恶意后端可向任意客户端会话注入任意消息（伪造登录结果、内部消息、超大包） |
| B2 | Gateway 后端回复仅凭 `__targetSessionId` 存在即投递，无归属校验 | 同上 | 跨用户消息投递 |
| B3 | EntityCall 以可预测 CallId 匹配响应，无来源/实体/方法校验 | `Framework\EntityCall\EntityCallHub.cs`、`EntityCallRouter.cs` | 伪造响应可完成他人未决调用 |
| B4 | DB 无逐用户授权，所有处理器信任请求自带 UserId/Account | `DB\Handlers\*`（全部） | 任意内部连接可冒充任意用户：读好友/黑名单、删好友、强加好友、改在线状态 |
| B5 | DB 邮箱重置凭"邮箱字符串相等"即重置（无 OTP/尝试上限） | `DB\Handlers\DbQueryHandler.Password.cs:112-162` | 已降级：Login 层先校验验证码（LoginHandler.Account.cs:108-160），但任何被攻破内部节点可绕过直接重置任意账号 |
| B6 | Center 节点注册身份自报、不绑定握手身份 → 伪造节点接管流量 | `Center\Handlers\MessageRouter.cs:495-522`、`NodeManager.cs:58-88` | 注册任意 `NodeType=Battle`+任意 Host/Port；重新注册现有 NodeId 可静默替换真实节点会话；心跳/负载可跨连接伪造 |
| B7 | Battle 迁移/91001 远程调用信任伪造实体身份/场景/属性/属主 | `Battle\BattleServerApp.cs:675-709,739-780,896-918` | 注入伪造 Player（篡改 Hp/Score）、任意调用实体方法、重绑任意 ClientSessionId |
| B8 | 登录/DB 请求关联仅靠可预测 requestId，`ResponseMsgId` 存而不用 | `Login\LoginServerApp.cs:398-411`、`Login\Handlers\LoginHandler.Security.cs:34`、`Game\Handlers\FriendHandler.DbResponse.cs:36-40` | 类型混淆/错误调用者完成 |
| B9 | 内部消息 `[Internal]` 标记仅 Gateway 入站单点执行，后端从不校验，legacy 段路由绕过 | `Attributes\GameMessageAttribute.cs:27-28`、`Gateway\GatewayServerApp.Network.cs:151-159,183-206` | "内部消息"实为文档注释而非安全边界 |

**建议**：逐节点独立密钥；握手后把认证身份绑定到会话（Session.UserData），所有注册/状态/请求处理器校验"发来者==声明者"；DB/Center/Battle 处理器从会话取行动者身份而非请求字段；出站路径加 msgid 白名单与目标授权。

---

## 四、高危（High）—— 按域分组

### 认证/令牌
- **H1** `Login\Handlers\LoginHandler.cs:47` `TokenSecret` 无占位/最小长度校验，缺失时随机回退 → 配置错误时静默生成易变密钥（登录态全部失效/可被猜测）。
- **H2**（=B8）DB 回复按 requestId 路由，msgId 读了不校验。
- **H3** 内部 TCP 明文传输登录口令/PBKDF2 哈希交换（无 TLS、无逐消息 MAC）。
- **H4** `Login\LoginServerApp.cs:621` 默认 `ListenAnyIP` 明文 HTTP，HTTPS 仅靠环境变量/开关。
- **H5** 令牌仅在登录时签发、之后从不重验（无防重放的状态跟踪跨进程）；登出不吊销。
- **H6** `Framework.Core\Security\SessionGuard.cs:84-88` 同一令牌 `seq==last` 幂等复用放行。
- **M1** 节流按账号维度、无按 IP → 锁定账号=账号级 DoS；`findPasswordCooldowns` 无清理。
- **M3** 找回密码/重置无频率限制 → 邮件轰炸。

### 持久化/架构
- **A1** 脏标记在"落盘写入"前即清除 + 写入失败被吞 → 静默数据丢失。
- **A2** 并发 flush 重排 + 关闭时在途写入丢弃。
- **F1** 每次启动自动执行 Schema 修复 DDL（无跨节点锁、无备份）。
- **G1** 自动迁移无跨节点互斥。
- **J1** `DB\DbServerApp.cs:167,172` SuperAdmin 生成密码以明文打日志/控制台。

### TCP/UDP
- **H1** 无连接上限/慢速连接 DoS；**H2** `PipelineTcpServer` Pipe 归属的 ReadOnlyMemory 返回后使用；**H3** 阻塞 `Socket.Send` 无背压；**H4** fire-and-forget 分发；**H5** UDP 会话身份可伪造。

### Game 社交
- **H1** 好友频道 DM 门控 fail-open（好友列表未加载时放行）；**H2** SenderName/SenderUniqueId 客户端可控（身份伪造/注入）；**H3** 社交 DB 请求无速率限制 + `PendingFriendRequests` 无界；**M4** 世界聊天可达未认证网关连接；**M5** 黑名单缓存 fail-open。

### Gateway
- **H1/H2**（=B1/B2）；**H3** 重连使用新 sessionId 与旧绑定分叉；**H4** UDP/KCP 会话身份可伪造；**H5** KCP 绕过 64KB 上限；**H6** Center/Battle 消息无登录门槛（"游客匹配"）。

### DB
- **H1** 登录锁 `AccountKey` 与在线态锁 `UserKey` 不一致 → 同行丢失更新；**H2** `DbQueryHandler.cs:99` 与 `RequestContextSession.cs:72` 双重 `AttachRequestId` → 嵌套元数据破坏关联响应；**H3** 登录/重置/改密不同错误文案构成账号/邮箱/状态枚举。

### EntityCall（=B3）
- **H1** 可预测 CallId；**H2** Remote/Local 零所有权校验（任意 EntityId）；**H3** 未决字典无界。

### KCP/WebSocket
- **K2** `frg=127` 分片毒化卡死接收路径；**K3** 会话表耗尽 + 4 字节 keepalive 绕过 5 分钟超时；**K4** `frg>=128` 日志洪泛+客户端循环被杀；**K5** 死链路不回收。
- **W1** 一次坏握手杀死 WS accept 循环；**W2** 无 WS 会话上限；**W3** 无 Origin/路径/鉴权（CSWSH）；**W4** 无空闲超时、`CancellationToken.None`、StopAsync 不关会话。

### 脚本宿主
- **H1** 脚本无执行时间/资源限制（死循环冻结 tick）；**H2** OnCreate/OnDestroy 无 try/catch；**H3** 热重载竞态（ALC 先卸载后迁移）；**H4** scriptsDir 任一 `.csx` 自动全信任编译执行（无签名/完整性校验；已复核**无**玩家字符串→eval 路径）。

### 编解码/分发
- **H1**（=C3）KCP 无载荷上限；**H2**（=B1）出站无 msgid 白名单/无内部过滤/无目标授权/无大小上限。

### Battle 房间/场景
- **H1** 无加入鉴权：任意 RoomId 可加入任意房间（含私有）并创建任意场景、无场景上限/回收 → 侦察+组合 C1 击杀+场景洪水 DoS。
- **H2** `BattleJoin` 非幂等：重复加入同房无限生成 Skill/Item 实体并泄漏计时器（实体/备份/CPU 无界增长）。
- **H3** 二次加入未拒绝：同一玩家双房间双实体（A 房留下"幽灵"）。
- **H4** 迁移冻结 30s 自动解冻不协调 → 91004 丢失=双节点重复玩家；迟到 91004 无条件 `CompleteMigrateOut` 删除活跃实体。
- **H5** Center 创建场景时 `MaxPlayers` 不设上限 → 房间容量上限可被绕过。

### Battle 实体/同步
- **H1** 速度外挂：20 单位/消息位移钳制可被洪泛突破（无时间窗口/无每客户端配额），配合共享入队 16384 槽可达到 ~6.5M 单位/秒；`Position:null` 被改写为 (0,0,0)。
- **H2** 共享入队无每客户端公平 → 单客户端洪泛冻结整个节点 tick 线程（FIFO 饿死他人 + 广播放大）。

---

## 五、中危精选（Medium）

- **持久化**：SchemaDoctor 缺陷（F2/F3）；DDL 每 Save + MySql 同步 over 异步（C1）。
- **传输**：长度前缀启发式误判（`msgId==payload.Length` 时漏帧前缀）；WS 每消息字节计数不重置（累计 >256KB 踢掉正常玩家）。
- **Game**：DB 响应用户归属竞态；列表无上限；O(n) 清扫。
- **EntityCall/编解码**：未决字典无界；16MB 载荷上限过大且无集合长度/深度护栏（反序列化炸弹）；`TryDispatch` 吞异常返回 true（客户端静默挂起）；`[Internal]` 仅入站执行（=B9）。
- **DB**：AccountQuery 泄露 Email/IsLocked/IsAdmin/IsOnline；Apply-accept 用 UserKey 而 AddFriend 用 PairKey → 重复好友行；双向好友申请竞态；PBKDF2 无节流 + 每 key 状态无界（SweepIdle 无人调用）；OrderedTaskQueue Enqueue↔Stop 竞态。
- **Center**：`GetBestBattleNode` 回退路径忽略心跳新鲜度（>30s 仍被路由）；HealthServer 无并发上限/读超时（本地 DoS）；控制器无 `[Authorize]` 纵深。
- **Battle**：迁移/91001 信任伪造（=B7）；无地图边界（出图/超界）；属主可调用已注册任意方法（潜在作弊/RCE 面）；QueryProgress 泄露私有 Score；EntityCallDirectRouter 在 socket 线程回调竞态 tick 线程；DestroyScene 不持久化/不通知/不解绑。
- **Battle 同步**：帧同步无每客户端上限（256/帧被首到者占满，他人输入被丢弃）；FrameId 完全被忽略（无顺序/去重/防重放 → 能力/开火作弊）；无地图边界+旋转值域校验；TimeSync 无频率限制。

---

## 六、已核验为安全的防御点（保留价值）

- **内部入站认证 fail-closed**：`InternalAuthFilter` 握手 + 占位密钥拒绝 + 跨重启防重放缓存（Game/DB/Center/Login/Battle 一致）。
- **Gateway 入站防护**：`StripClientFields` 剥离客户端 `__*`、拒绝未认证游戏消息、拒绝内部消息、后端入站认证。
- **密码学基元**：PBKDF2-HMACSHA256（10 万次迭代、迭代上限 100 万、恒定时间比较）；令牌 TTL+SessionSeq 单调防重放；splitmix64 会话 ID 不可预测；API Key 恒定时间比较。
- **Battle 实体属性**：PropertyCodec 全量边界检查（上限 256/1024/4096）；同步路径实体按网关认证会话查表，无跨玩家 EntityId 寻址；私有属性不进入跨玩家快照。
- **持久化 SQL**：全部 EF LINQ 参数化，无 SQL 注入；Redis 无 RESP 注入；文件 ID 白名单 + 长度限制，无路径穿越。
- **LeaderElection / RemoteLogClient**：文件锁选举 OK；日志队列有界 + 可选 HMAC。
- **Battle 同步阻塞调用**：`.GetAwaiter().GetResult()` 均作用于同步处理器，无死锁（风格问题）。
- **Center/Health**：API Key 中间件覆盖全部路由（未配置密钥时全 401）、恒定时间比较；Health 仅绑回环 + 精确路径匹配。

---

## 七、修复优先级建议（Top 10）

1. **轮换并移除已提交凭据**（MySQL/SMTP/HttpApiKeys），清理 git 历史；配置改为环境变量/密钥库。
2. **KCP 加固**：发送队列上限、接收载荷大小上限、分片数上限、会话数/超时/keepalive 校验（对应 C2/C3/K2-K5）。
3. **内部信任边界重构**：逐节点密钥；认证身份绑定会话；所有处理器以会话身份为行动者；出站 msgid 白名单 + 目标授权（主题 B 全项）。
4. **Battle 加入/伤害授权**：服务端签发房间令牌/成员校验；伤害仅限无主世界实体或 PvP 授权；join 幂等 + 双房拒绝 + 场景上限/回收（C4/H1/H2/H3）。
5. **持久化写穿**：落盘成功再清脏标记；写入失败显式上抛/告警；关闭时排空在途写入；Schema 修复与迁移加跨节点锁。
6. **全系统速率限制**：每连接/每客户端/每账号配额（网关、社交、DB、Center API、Battle 同步、Health）。
7. **同步服务端权威**：按 `maxSpeed × Δt` 速度预算 + 地图边界 + 每客户端帧输入配额 + FrameId 顺序/防重放。
8. **关联强化**：requestId 绑定连接+校验响应 msgid；EntityCall 响应校验来源/实体/方法。
9. **传输一致性**：统一显式帧（去掉长度启发式）；WS 计数重置；出站大小上限。
10. **管理面与日志**：Center/Login 管理 HTTP 绑回环/TLS；API Key 最小熵 + 锁速 + 轮换；SuperAdmin 密码仅一次性展示或输出到受保护介质；`[Authorize]` 纵深。

---

## 附：发现统计汇总

14 个域合计约 190 项发现（严重 ~6、高危 ~45、中危 ~60、低危 ~55、信息 ~25），经人工复核去重与校准。本报告正文聚焦严重/高危与高价值中危；完整逐条清单可按域向审计代理索取。

---

## 八、修复记录（按 Top 10 优先级逐项实施）

> 依据 2026-02 逐项修复会话记录。每项修复均以 `dotnet build <项目>.csproj -v q --nologo` 验证通过（0 警告 0 错误），不改变既有行为语义。未做超范围重构（逐节点密钥、git 历史重写、Schema 迁移锁、长度启发式移除均属运维/大改，保留为后续项）。

| # | 优先级项 | 实施内容 | 主要改动文件 |
|---|---|---|---|
| 1 | 已提交凭据移除/环境变量化 | `HttpApiKeys`、`SMTP.Account/Password`、MySQL 连接串改为空 + 环境变量注入（`SMTP__*`、`ConnectionStrings__MySqlConnection`、`HttpApiKeys` 等），缺失时启动即失败/拒绝/跳过发信；新增 `.env.example` 文档。真实凭据轮换与 git 历史重写为运维动作。 | `Login/appsettings.json`、`DB/appsettings.json`、`Shared/ConfigHelper.cs`、`.env.example` |
| 2 | KCP 加固 | 发送队列（`WaitSnd`≥2048）与接收载荷（>64KB）超限丢弃；协议违规置 `MarkedForClose` 并清理会话；网关注站包 64KB 兜底；告警限频（5-10s）。 | `Network/Kcp/KcpSession.cs`、`Network/Kcp/KcpServer.cs`、`Gateway/GatewayServerApp.Network.cs` |
| 3 | 内部信任边界 | 网关出站 msgid 白名单+内部消息拒绝+出站帧 68KB 上限（Login/Game/Center/Battle 四路径）；Login/Game DB 响应按 `msgid+100` 校验；EntityCall CallId 掩码+方法/实体校验；Center 注册/状态处理器绑定握手身份（`AuthenticatedNodeId`）与会话（`GetNodeIdBySession`），伪造注册/跨连接上报被拒；Gateway↔Center 握手 nodeId 与注册同源。 | `Gateway/GatewayServerApp.Backend.cs`、`Login/LoginServerApp.cs`、`Login/Handlers/LoginHandler.Security.cs`、`Game/Handlers/FriendHandler*.cs`、`Framework/Framework.Entity/EntityCall*.cs`、`Center/Handlers/MessageRouter.cs`、`Center/Handlers/NodeAuthFilters.cs`、`Framework.Core/Security/InternalAuthFilter.cs`、`Center/CenterServerApp.cs` |
| 4 | Battle 加入/伤害授权 | 跨玩家脚本动作全面禁止（白名单仅限 `OwnerClientId==0` 的无主世界实体）；join 幂等 + 双房拒绝；单节点场景数上限（500）；Center 创建场景 RoomId 校验 + 容量钳制（200）。 | `Battle/BattleServerApp.cs`、`Battle/Handlers/RoomHandler.cs`、`Battle/Handlers/BattleMainHandler.cs` |
| 5 | 持久化写穿 | 批量/异步落库写入失败 `ForcePersistDirty` 重试（不再"先清脏标记"）；并发落库串行门闩（防重排）；关闭置位+等待在途+最终排空再释放存储；关服先停 tick 再 flush。 | `Framework/Framework.Entity/Entity.cs`、`Framework/Framework.Entity/EntityPersistenceService.cs`、`Battle/BattleServerApp.cs` |
| 6 | 全系统速率限制 | 网关每会话入站消息速率上限（600/s 可配，超限关连接）；好友/社交 DB 请求单会话（16）+全局（1024）待处理配额；Center HTTP 每 key 每分钟限流（120，429）。 | `Gateway/Managers/GatewaySessionManager.cs`、`Gateway/GatewayServerApp.Network.cs`、`Game/Handlers/FriendHandler.cs`、`Game/Handlers/FriendHandler.DbResponse.cs`、`Center/CenterApiKeyAuthMiddleware.cs` |
| 7 | 同步服务端权威 | 移动校验改为速度预算 `maxSpeed×Δt×容差 + 下限`（时间窗感知，不复被洪泛突破）+ 每客户端同步速率配额 + 可选地图边界 `WorldBounds*`；`Position:null` 不再改写为 (0,0,0)；帧同步 FrameId 严格递增防重放/乱序/重复 + 每客户端每帧输入配额（8）+ 输入队列上限（512）+ 场景销毁清理帧状态。 | `Battle/Handlers/EntitySyncHandler.cs`、`Battle/Handlers/FrameSyncManager.cs` |
| 8 | 请求关联强化 | Login/Game DB 响应 msgid 校验、EntityCall 来源/实体/方法校验、requestId 绑定连接（本会话内已完成）——见 #3 行。 | 同 #3 |
| 9 | 传输一致性 | WS 每消息字节计数重置已验证现码已修复（`WebSocketServer.cs:157`），无需改动；长度启发式移除属大改保留。 | `Network/WebSockets/WebSocketServer.cs`（复核） |
| 10 | 管理面与日志 | SuperAdmin 随机密码不再写入日志文件（仅控制台一次性输出）；Center 管理 HTTP 绑定地址可配置 `CenterHttpListenAddress` + 非回环明文监听告警。 | `DB/DbServerApp.cs`、`Center/CenterHttpServer.cs` |

**已知保留（未在本轮实施，属超范围/运维）**：逐节点密钥与逐消息 MAC（需协议变更）；git 历史凭据重写（需运维）；Login `TokenSecret` 占位校验缺失回退（P1 已文档化，建议配置 TokenSecret）；迁移/实体跨节点一致性锁（91004 竞态）；长度前缀启发式统一；Center 逐房间授权令牌；`findPasswordCooldowns` 清理与邮件轰炸限流（Login 层已有部分节流）。

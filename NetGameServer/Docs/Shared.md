# Shared 共享基础库

> 跨节点复用的公共模型、配置、消息 DTO、日志与序列化工具。
> 业务节点和框架层都依赖 Shared；Shared 不依赖任何业务节点。
> 项目总览见 [README.md](../../README.md)，编码规范见 [Code-Style.md](Code-Style.md)。

## 职责边界

- ✅ 公共配置加载（`ConfigHelper`：appsettings + 环境变量 `NG_` 前缀 + 内存覆盖 + 节缓存 + 校验 + 热重载）
- ✅ 公共 JSON 序列化辅助（`Shared.Json.SerializeToUtf8Bytes` / `DeserializeFromUtf8Bytes`）
- ✅ 统一日志接口（`Shared.Log` 包装 Serilog；`RemoteLog` 远程日志）
- ✅ 路由元数据辅助（`RouteMetadata`：Gateway↔后端）
- ✅ UID / UUID 生成器（`UIDGenerator` 玩家全局递增 UID、`UUIDHelper`）
- ✅ Redis 客户端辅助（`RedisHelper`）
- ✅ 节点启动参数（`NodeLaunchArgs`：--port / --host / --center-host / --node-id / --instance-id / --machine-id / --supervised-by）
- ✅ 业务消息 DTO（Login / Game / Chat / Friend / Center / Battle / Db / Social / Special）
- ✅ 业务数据模型（User / Friend / Blacklist / FriendRequest / ChatMessage / MessageIds）
- ❌ 不引用任何业务节点（`Battle` / `Game` / `Center` / ...）
- ❌ 不写业务逻辑

## 入口与启动

Shared 是 **class library**，不直接启动；被 `Battle` / `Game` / `Center` / `Login` / `Gateway` / `DB` / `Framework.*` 引用。

## 关键文件

| 文件 | 职责 |
|---|---|
| `Shared/ConfigHelper.cs` | 配置加载（appsettings + env `NG_*` + 内存源）+ 节缓存 + 校验 + 热重载 |
| `Shared/Json.cs` | 统一 JSON 序列化辅助（兼容旧 JSON 客户端） |
| `Shared/Log.cs` | 日志接口（Serilog 门面） |
| `Shared/RemoteLog.cs` | 远程日志上报 |
| `Shared/RouteMetadata.cs` | 路由元数据（`__clientSessionId` / `__userId` / `__uid` / `__broadcast`） |
| `Shared/UIDGenerator.cs` | 玩家 UID 全局递增 |
| `Shared/UUIDHelper.cs` | UUID 生成 |
| `Shared/RedisHelper.cs` | Redis 客户端辅助（缓存 / 分布式限流计数器） |
| `Shared/NodeLaunchArgs.cs` | 节点启动 args 通用解析（被 Machine / Supervisor / 各节点 Program.cs 共用） |
| `Shared/Messages/MessageIds.cs` | 协议 MsgId 常量（由 `Framework.Protocol.Generator` 从 `Framework.Protocol/Messages/*.cs` 的 `[GameMessage]` 声明编译期生成） |
| `Shared/Messages/*` | 业务消息 DTO（按域分目录：Battle / Center / Chat / Db / Login / Social / Special） |
| `Shared/Data/*` | 业务数据模型（User / Friend / Blacklist / FriendRequest / ChatMessage） |

## 注意事项

- **零业务依赖**：Shared 不引用任何业务节点，只引用 `Framework/*` 和 `Network`；`Framework.Entity` / `Framework.Protocol` 都可以引用 Shared。
- **协议消息走源生成器**：业务消息的强类型由 `Framework.Protocol.Generator` 从 `[GameMessage]` 声明编译期产出（`Framework/Framework.Protocol/Messages/*.cs`），Shared 这里只放非协议 DTO（请求/响应的"业务对象"，如 `FriendInfo`）。DTO 与协议消息类**不重名**避免冲突。
- **Json 序列化**：仅用于兼容旧 JSON 客户端（`MessageDispatcher` 的 `jsonFallback: true` 路径）；新业务消息用 MemoryPack（生成类自带 `Serialize()` / `Deserialize()`）。
- **不要在 Shared 写业务逻辑**：业务逻辑放 `Battle/Handlers` / `Game/Handlers` 等。
- **DTO 命名**：业务对象用 PascalCase，与 Generated 消息类**不重名**。

## 排错

| 症状 | 可能原因 | 排查 |
|---|---|---|
| 业务节点编译失败找不到类型 | 用了 Generated 消息类但没引用 `Framework.Protocol` | 在业务节点 csproj 加 `Framework.Protocol` 引用 |
| DTO 字段改了客户端没生效 | 客户端用 Generated 消息类 | 重新构建 + 客户端重新生成 |
| Json 反序列化失败 | 用了非 MemoryPack 字段 | 用 `[MemoryPackable]` 标注 / 确认字段类型兼容 JSON |
| 配置项改了不生效 | 节缓存未刷新 / 热重载未触发 | 检查 `ConfigHelper.RegisterValidator` / `OnConfigChanged` 订阅 |

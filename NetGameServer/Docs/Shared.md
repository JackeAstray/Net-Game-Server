# Shared 共享基础库

> 跨节点复用的公共模型、配置、消息 DTO、日志与序列化工具。
> 业务节点和框架层都依赖 Shared；Shared 不依赖任何业务节点。

## 主要内容

| 模块 | 说明 |
|---|---|
| `Config/` | `ConfigHelper` 统一配置加载（appsettings + 环境变量 `NG_` 前缀） |
| `Messages/` | 业务消息 DTO（Login/Game/Chat/Friend/...） |
| `Data/` | 业务数据模型（Player/Social/Chat/...） |
| `Json` | 统一 JSON 序列化辅助（`Shared.Json.SerializeToUtf8Bytes` / `DeserializeFromUtf8Bytes`） |
| `Log` | 统一日志接口（Serilog） |
| `RouteMetadata` | 路由元数据辅助（Gateway↔后端） |
| `EntityUidGenerator` | 玩家 UID 生成器（全局递增） |

## 设计原则

- **零业务依赖**：Shared 不引用任何业务节点（`Battle`/`Game`/`Center`/...），
  只引用 `Framework/*` 和 `Network`。
- **可被低层引用**：`Framework.Entity` / `Framework.Protocol` 都可以引用 Shared。
- **协议消息走 Generated**：业务消息的强类型由 Protogen 生成到 `Framework.Protocol/Generated/`,
  Shared 这里只放非协议 DTO（请求/响应的"业务对象"，如 `FriendInfo`）。

## 注意事项

- **不要在 Shared 写业务逻辑**：Shared 是工具集，业务逻辑放 `Battle/Handlers` / `Game/Handlers` 等。
- **DTO 命名**：业务对象用 PascalCase，DTO 与 Generated 消息类**不重名**避免冲突。
- **Json 序列化**：仅用于兼容旧 JSON 客户端（`MessageDispatcher` 的 `jsonFallback: true` 路径），
  新业务消息用 MemoryPack（生成类自带 `Serialize()` / `Deserialize()`）。

## 排错

| 症状 | 可能原因 | 排查 |
|---|---|---|
| 业务节点编译失败找不到类型 | 用了 Generated 消息类但没引用 `Framework.Protocol` | 在业务节点 csproj 加 `Framework.Protocol` 引用 |
| DTO 字段改了客户端没生效 | 客户端用 Generated 消息类 | 重新构建 + 客户端重新生成 |
| Json 反序列化失败 | 用了非 MemoryPack 字段 | 用 `[MemoryPackable]` 标注 / 确认字段类型兼容 JSON |

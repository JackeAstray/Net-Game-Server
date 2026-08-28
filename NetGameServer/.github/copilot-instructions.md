# Copilot Instructions

> GitHub Copilot / AI 助手在为本仓库生成代码与文档时应遵守的约定。
> 完整架构与节点说明见 [README.md](../../README.md)、[Docs/*.md](../Docs/)。

## 项目原则

- **协议稳定优先**：客户端应匹配服务端协议，不要改服务端去适配客户端。按服务端 `Protocol/defs/*.def` 定义实现客户端行为与报文格式。
- **多协议接入**：Gateway 同时监听 TCP / UDP / KCP / WebSocket 四种客户端协议，统一通过 `ISession` 抽象汇聚 `OnSessionConnected` / `OnDataReceived` / `OnSessionDisconnected` 事件，再走 `RouterTable` 分发到 Login/Game/Center/Battle。
- **强类型消息**：业务消息先在 `Protocol/defs/*.def` 声明，构建时由 `Protogen` 生成强类型类与 `MessageIds` 常量；业务层在 `MessageDispatcher` 注册 `RegisterAsync<TReq, TRes>`，**不要**在 Handler 内再次手动反序列化。
- **路由元数据**：Gateway 注入 `__clientSessionId` / `__userId` / `__uid` / `__broadcast`，后端用 `RouteMetadata.TryExtract*` 解析。链路格式见 [Protocol.md](../Docs/Protocol.md)。
- **JSON 兼容**：`MessageDispatcher.RegisterAsync(..., jsonFallback: true)` 兼容旧 JSON 客户端；新业务消息统一走 MemoryPack。**不要**直接调 `System.Text.Json.JsonSerializer`，统一用 `Shared.Json.SerializeToUtf8Bytes` / `DeserializeFromUtf8Bytes`。
- **登录链路**：客户端经 Gateway 走 `[MsgId(4)][Payload]`（10001=登录、10003=注册），由 Login 节点 `LoginHandler` 处理；HTTP 仅用于无状态管理面接口（如果有），不是客户端主路径。
- **内部消息保护**：91001~91006、90999 等 `internal="true"` 消息由 Gateway 拒绝客户端伪造；不要在公共客户端协议里复用内部 MsgId。

## 文档语言

- 仓库已有文档以中文为主；新增文档默认中文（除非用户明确要求其他语言）。
- 文档改动需保持与 [Code-Style.md §八 提交流程](../Docs/Code-Style.md) 一致：改文档 → 改代码 → 跑六套验证套件。

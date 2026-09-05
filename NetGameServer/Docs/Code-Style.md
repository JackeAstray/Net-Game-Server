# 编码规范与约定

> 仓库内的代码组织、入口写法、命名风格、注释密度的统一约定。
> 新增代码前请读一遍；与本约定冲突的代码应改成本约定一致。

---

## 一、入口写法：所有 `Program.cs` 用 `class + static Main`

### 1.1 `class + static Main`（唯一约定）

**用途**：所有 `OutputType=Exe` 的项目入口（节点服务器、工具、测试套件）。

每个 `Program.cs` 显式包成 `namespace <目录名>;` + `internal static class Program` + `static async Task<int> Main(string[] args)`。
这样：

- 入口语义显式（一眼看出这是进程入口，不是普通类）
- 嵌套的辅助类（DTO / 工具类）以 `internal sealed class` 形式直接放在 `Program` 内
- 与其他业务/框架 `.cs` 写法一致（`namespace + class`）
- IDE 跳转、StackTrace、断点定位更稳定

**典型场景**：

- 节点入口（`Gateway/Program.cs` / `Login/Program.cs` / ...）：配置加载 + `App.Run()`
- 工具入口（`Tools/Supervisor/Program.cs` / `Tools/Machine/Program.cs` / `Tools/ClientGen/Program.cs`）：参数解析 + 执行
- **测试套件**（`Tests/ProtocolVerify/Program.cs` 等）：20~30 段验证串联，共享变量
  与中间状态放在 `Main` 方法体内（顶级语句风格的代码块可以无缝塞进 `Main`）

**禁止**：

- ❌ 顶级语句（`OutputType=Exe` 项目里也用 `class + static Main`，不要依赖 .NET 自动合成入口）
- ❌ `internal static class Program` 同时被其他项目的验证套件通过 `XXX.Program.Main(...)` 跨项目调用 —— 跨项目被调用的工具入口必须是 `public static class Program` + `public static Main`（如 `Tools/Supervisor` / `Tools/Machine` / `Tools/ClientGen`，分别被 `Tests/SupervisorVerify` / `Tests/MachineVerify` / `Tests/ClientGenVerify` 调用）
- ❌ 嵌套在 `Program` 内的 public DTO 类被外部依赖时仍藏在 `Program` 里（应抽到独立 `.cs`）

### 1.2 模板

```csharp
using ...;

namespace Gateway;   // 根命名空间 = 目录名

/// <summary>
/// 网关服务器入口：一句话描述。
/// </summary>
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        // 业务代码（顶级语句风格的代码块直接放在这里）
        ...
        return 0;
    }
}
```

工具/同步入口用 `static int Main(string[] args)`（无 `async`）；异步入口用 `static async Task<int> Main`。

### 1.3 `Program.cs` 与其他 `.cs` 的关系

`Program.cs` 文件本身**可以执行**（`OutputType=Exe`），这与"业务代码"放在其他 `.cs` 里
（class、namespace、partial）并不冲突：业务代码是 **library**（被节点项目引用），
`Program.cs` 是 **executable entry**（进程启动点）。两者的写法约定相同（都是 `namespace + class`）：

| 类型 | 文件 | 写法 | 例子 |
|---|---|---|---|
| 入口 | `*/Program.cs` | `namespace + internal static class Program` + `static (async Task<int>)? Main` | `Gateway/Program.cs` |
| 业务 | `*/Handlers/*.cs` / `*/Managers/*.cs` | `namespace + class` | `Battle/Handlers/RoomHandler.cs` |
| 框架 | `Framework/*/*.cs` | `namespace + class` | `Framework/Framework.Entity/Entity.cs` |
| 脚本 | `GameLogic/scripts/*.csx` | `EntityScriptBase` 继承 + 顶级 `return new XxxScript()` | `Skill.csx` |

---

## 二、命名与文件组织

- **命名空间**：根命名空间 = 目录名（`namespace Battle;` / `namespace Framework.Entity;`）。
- **文件名**：1 个主类 = 1 个 `.cs`（`RoomHandler.cs` 里只有 `RoomHandler`）。
  例外：partial 类可分多个文件（`GatewayServerApp.Network.cs` / `.Backend.cs` / `.CenterClient.cs`）。
- **partial 类**：超大节点入口按职责拆 partial（`GatewayServerApp.*.cs`），避免单文件 1000+ 行。
- **静态类** vs **单例**：无状态工具类用 `static class`（`TokenService` / `EntityCallHub` /
  `SessionGuard`）；有状态管理器用 `class + public static Instance`（`NodeManager.Instance`）。

---

## 三、注释与文档密度

- **公共 API**（public 方法/类）：必须有 XML 文档注释（`<summary>` + 关键 `<param>` / `<returns>`），
  描述**做什么、为什么、与 KBE 的对标关系**（如 `/// 对标 KBE Mailbox`）。
- **关键实现**：解释**为什么这样做**（不是做什么），便于后人理解设计取舍。
- **私有方法**：简短一行注释说明意图即可。
- **不要**：在每个文件顶部写冗长文件头注释（项目 README 已经说过的事）。

---

## 四、错误处理

- **不吞异常**：`catch (Exception) { }` 是反模式。如确需吞，必须注释说明为什么。
- **日志先行**：`catch` 内必须先 `Log.Error(ex, "上下文 SessionId:...")`，再决定是否重抛 / 走降级。
- **启动期失败**：必须 `Log.Error` + 抛出，让进程退出；不要静默继续。
- **运行期失败**：节点内 catch 后 Log + 走降级（断连 / 回默认值），**不退出进程**——单个会话失败不应拖垮全节点。

---

## 五、并发与状态

- **Battle 节点**：实体/场景状态**只在 tick 线程读写**。其他线程（收包 / 心跳）只入队，
  由 `DrainInboundMessages` 串行消费。**绝不**在 `OnDataReceived` 等回调里直接动实体。
- **共享集合**：跨线程用 `ConcurrentDictionary` / `ConcurrentQueue`；否则用 lock。
- **CAS 循环**：高频更新用 `Interlocked.CompareExchange` / `TryUpdate` 避免锁。
- **TimeProvider**：测试可注入时间（`SessionGuard.IsSessionValid(now)`），
  不要在业务代码里直接 `DateTime.UtcNow` 让你无法单测。

---

## 六、协议与序列化

- **协议声明**：所有消息在 `Framework/Framework.Protocol/Messages/*.cs` 用 C# `[GameMessage]` / `[GameStruct]` 声明，
  由 `Framework.Protocol.Generator`（Roslyn 源生成器）编译期产出 `MessageIds` / `RouterTable` / `IGameMessage` 管线 /
  `ProtocolManifest.json`（供 ClientGen）。`Protocol/defs/*.def` 已迁移为 ID 段占位，**不要**在里面加消息。
  **禁止**手改 `Framework.Protocol/Generated/*.g.cs`（构建时重生成）。
- **新消息**：在 `Messages/*.cs` 写 `[GameMessage]` 类 → 构建一次 → 用生成的 `MessageIds` / 类型，并在目标节点 `Dispatcher` 注册。
- **二进制序列化**：用 MemoryPack（`Serialize()` / `Deserialize()` 来自 `IGameMessage`），
  业务层**不要**手 `Json.SerializeToUtf8Bytes`——除非有兼容旧客户端的明确理由（`jsonFallback: true` 已在 MessageDispatcher 处理）。
- **跨节点消息**：90001~90010 / 90999 / 91001~91010 等走 `internal="true"`，不接受客户端伪造（Gateway 拒绝）。

---

## 七、依赖与可见性

- **public 谨慎**：框架类用 `public` 是为了被多项目引用；业务类如无外部引用需求
  优先 `internal`（`FriendHandler` 等已 partial + internal）。
- **项目引用**：低层 → 高层单向（`Framework.Entity` 不引用 `Battle`），不要反向。
- **测试项目**：可以引用任何项目（测试就是要测）；但测试代码不进主项目编译。

---

## 八、提交流程

1. 改代码 → 改对应文档（README / Docs/*.md）
2. `dotnet build NetGameServer.slnx` → 0 错误
3. 跑八套验证（命令见 [README.md](../../README.md) §快速开始）：
   - ProtocolVerify / NetworkVerify / ScriptHostVerify / LoggerVerify / SupervisorVerify / MachineVerify / LifecycleVerify / ClientGenVerify
4. 提交信息格式：`迭代N：<主题>` 或 `fix: <主题>` / `doc: <主题>` / `refactor: <主题>`
5. 单 commit 单主题，避免大杂烩

---

## 九、补充资源

- `Docs/KBE-Gap-Review.md` — 与 KBEngine 的能力差异与可优化路线
- `Docs/Protocol.md` — 协议约束红线
- `GameLogic/scripts/README.md` — 业务脚本层规范
- `README.md` — 项目总览与产品形态

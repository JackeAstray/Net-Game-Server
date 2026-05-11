# DB 项目详细架构与技术文档

## 一、 项目定位
`DB` 为业务数据持久化中心层，它是 `Net-Game-Server` 的控制台应用级别的独立组件。可以直接充当内部独立的 Data-Saving 存储微服务跑起来（消费其他战斗服异步发来的战报结果），又能同时作为核心的 Entity Framework (EF Core) DDL 模型建立与版本迭代的管理模块，被 `Game` 或 `Login` 通过注入直接引用底层业务表结构。

## 二、 核心技术栈与依赖
- **Entity Framework Core 9.0**: (.NET 10 兼容版本)，进行 O/RM 的双向映射管理工作。包含 `Microsoft.EntityFrameworkCore.Design` 和 `Tools` 支撑命令行创建更新（`dotnet ef migrations add...`）。
- **Pomelo.EntityFrameworkCore.MySql**: 是非常流行且性能极高的高级第三方 MySQL EF 驱动程序。它表明该游戏持久层使用的关系型数据库核心架构主要是基于 MySQL （或 MariaDB）。
- **Microsoft.Extensions.DependencyInjection** 和 **Configuration**: 加载 `appsettings.json` 中不同集群运行图（测试服/生产环境）环境内的外部连接字符串、密码安全配置。内置的 DI。

## 三、 详细模块拆解

### 1. ORM 上下文管理 (`DefaultDbContext.cs`)
- 核心功能：代表着应用层的游戏实体和关系数据库持久层的虚实桥梁转换。
- **表建模配置（Model Creation）**: 重载 `OnModelCreating`，配置所有数据表（如 Players、Inventories 等）的生成限制如：长度、主键自增策略、外键级联约束乃至联合唯一性键验证。
- **配置与初始化**: 接入 MySQL 连接池以及 EF 指令查询日志重定向输出至我们 `Shared` 项目指定的 `ILogger` 服务上。

### 2. DDL 迁移应用入口 (`Program.cs`)
由于其类型为 `<OutputType>Exe</OutputType>`，该节点常常承担下面两种任务状态：
- 形式 A (运维态): 开发期或者布署环境升级时，直接启动此 Exe 传入特殊启动参数用于初始化/检查数据库是否结构同步（Auto Apply Migration）。
- 形式 B (运行态): `RunAsync()` 挂起为一个后台承载的异步落地服务，利用 `Network` 项连接进微服务生态，接受消息把非核心非敏感的诸如 `日志操作`，`背包整理更新队列` 进行消费存库。

### 3. 数据层通讯协议支持 (`Shared.Messages.Db` 引用)
数据库服务层直接接收并消费从 `Login` 等微服务投递过来的验证请求模型（例如 `LoginVerifyRequest` 或 `GetMaxUidRequest`），统一进行验证和读取逻辑后，向源服务返回序列化后的响应报文（例如包含有效 UserId 或错误说明）。

## 四、 开发规范
1. **防延迟分离设计**: 为了维护游戏的帧顺畅：业务服内如果发生了持久化事务相关修改（例如升级获取金币等结算），**禁止在此刻进行强堵塞 Wait**的直接 SaveChanges 同步网络落地操作。它应该只作为内存修改然后挂起写入队列缓存池并立刻响应玩家，后台定时将大批量 EF 上下文汇聚一次 Save，或者交由独立的 MQ 同步分发进程解决。

# Network 网络库文档

## 简介

`Network` 项目是一个基于 **.NET 10** 的高性能网络通信库，专为游戏服务器微服务架构（如：网关服务器、中心服务器、逻辑服务器等）设计。它集成了多种业内流行的底层及高层网络技术，旨在为您提供稳定、灵活且具有极高吞吐量的多场景网络连接方案。

## 核心特性与技术栈

- **MagicOnion & gRPC**：提供极高清晰度的类型安全 RPC 调用，支持双向流式通信，避免手写 `.proto` 的繁琐过程，直接利用 C# 接口共享契约。
- **KCP 协议支持 (`Kcp`)**：基于 UDP 的可靠传输协议。相比 TCP 具有更优的弱网环境抗抖动能力和极低延迟特性，非常适合战斗、位置同步等强实时性需求的模块。
- **分布式消息总线 (`NetMQ`)**：ZeroMQ (NetMQ) 用于高性能的消息队列和跨进程通信，适用于游戏服集群内部极速的消息分发、广播以及进程间通信（IPC）。
- **高性能API网关 (`YARP.ReverseProxy`)**：强大的反向代理及高性能网关，支持高级别的动态负载均衡和连接治理。可以非常平滑地对 WebSocket、gRPC 等长连接进行代理。
- **内存优化 (`Microsoft.IO.RecyclableMemoryStream`)**：采用对象池方式复用内存流，大幅减少大对象堆（LOH）触发大型垃圾回收的压力，提高服务器持续高并发能力。
- **弹性与容错处理 (`Polly.Core`)**：用于构建针对外部微服务调用的失败重试策略、断路器、超时回退等，显著增加多节点服务容错能力。

---

## 模块介绍与代码示例

### 1. 微服务 RPC 通信 (MagicOnion + gRPC)

通过复用 `Shared` 项目中定义的 C# 接口，在服务端和客户端之间产生类型安全的调用链路，无需生成额外的文件。

#### ① 定义契约 (在 Shared 项目中)
```csharp
using MagicOnion;

public interface ILoginService : IService<ILoginService>
{
    // UnaryResult 是 MagicOnion 提供的高效异步返回值类型
    UnaryResult<string> LoginAsync(string account, string password);
}
```

#### ② 服务端实现 (在具体的 Service 实现层)
```csharp
using MagicOnion;
using MagicOnion.Server;

public class LoginService : ServiceBase<ILoginService>, ILoginService
{
    public async UnaryResult<string> LoginAsync(string account, string password)
    {
        // TODO: 数据库校验账号密码...
        if (account == "admin" && password == "123456")
        {
            return "Token_Generated_Success";
        }

        throw new ReturnStatusException(Grpc.Core.StatusCode.Unauthenticated, "账号或密码错误");
    }
}
```

#### ③ 客户端/其它微服务调用
```csharp
using Grpc.Net.Client;
using MagicOnion.Client;

// 连接到提供服务的节点或网关
var channel = GrpcChannel.ForAddress("https://localhost:5001");
var loginClient = MagicOnionClient.Create<ILoginService>(channel);

// 透明地进行 RPC 调用
var token = await loginClient.LoginAsync("admin", "123456");
Console.WriteLine($"登录并获取Token: {token}");
```

### 2. 实时对战/低延迟通信 (KCP)

主要用于 FPS, MOBA, 或格斗游戏等对丢包与网络延迟高度敏感的模块。

```csharp
using System.Net.Sockets;

public class KcpSession
{
    private readonly Kcp _kcp;
    private readonly UdpClient _udpClient;

    public KcpSession(uint conv, UdpClient udpClient)
    {
        _udpClient = udpClient;

        // 传入 会话编号 (conv) 并初始化
        _kcp = new Kcp(conv, user: null);

        // 启动极速模式: nodelay=1, interval=10ms, resend=2, nc=1
        _kcp.NoDelay(1, 10, 2, 1);

        // 配置底层发送方法回调
        _kcp.SetOutput((data, length, user) => 
        {
            // 通过底层 UDP 发送出网络
            _udpClient.Send(data, length);
        });
    }

    /// <summary>
    /// 定期调用以驱动 KCP 的状态机
    /// </summary>
    public void Update(uint currentTimeMs)
    {
        _kcp.Update(currentTimeMs);
    }

    /// <summary>
    /// 被外部调用，下发游戏帧数据流
    /// </summary>
    public void SendData(byte[] buffer)
    {
        _kcp.Send(buffer);
    }

    /// <summary>
    /// 接收到底层 UDP 包时调用，塞入 KCP
    /// </summary>
    public void OnUdpPacketReceived(byte[] buffer, int length)
    {
        _kcp.Input(buffer, 0, length);
    }
}
```

### 3. 高并发内部消息总线 (NetMQ)

当不需要复杂的 RPC 契约，只需要在服务器集群间广播某些事件（例如全服跑马灯、公会踢人下线），ZeroMQ 提供了极高效率的支持。

#### 发布事件代码 (发布者/中心发令服务器)
```csharp
using NetMQ;
using NetMQ.Sockets;

// 绑定端口进行事件发布
using (var pubSocket = new PublisherSocket("@tcp://*:12345"))
{
    // 发送主题与具体内容
    pubSocket.SendMoreFrame("GlobalNotice").SendFrame("全服活动已开启！");
}
```

#### 订阅事件代码 (订阅者/各区逻辑服)
```csharp
using NetMQ;
using NetMQ.Sockets;

using (var subSocket = new SubscriberSocket(">tcp://localhost:12345"))
{
    // 订阅特定的频道
    subSocket.Subscribe("GlobalNotice");

    // 阻塞/异步等待数据接收
    var topic = subSocket.ReceiveFrameString();
    var msg = subSocket.ReceiveFrameString();

    Console.WriteLine($"收到 [{topic}] 的广播: {msg}");
}
```

### 4. 网关代理与负载均衡 (YARP)

YARP 可以轻松将网关处的外部请求以极低损耗路由至后面的微服务节点中，支持非常完备的负载均衡策略。

**appsettings.json (配置路由与目标集群)**:
```json
{
  "ReverseProxy": {
    "Routes": {
      "grpc_route": {
        "ClusterId": "chat_cluster",
        "Match": {
          "Path": "/ChatService/{**catch-all}"
        }
      }
    },
    "Clusters": {
      "chat_cluster": {
        "Destinations": {
          "chat_instance_1": {
            "Address": "https://localhost:10001/"
          },
          "chat_instance_2": {
            "Address": "https://localhost:10002/"
          }
        }
      }
    }
  }
}
```

**入口注册 (Program.cs)**:
```csharp
var builder = WebApplication.CreateBuilder(args);

// 基于配置加入 YARP 代理服务
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

// 应用代理中间件
app.MapReverseProxy();
app.Run();
```

### 5. 高性能内存与异常重试处理 (性能规范与容错)

#### 最佳实践: RecyclableMemoryStream
普通 `MemoryStream` 有可能实例化导致 LOH (大对象堆)，而在频繁的帧同步/数据编解码中，使用内存池可以把 GC 带给玩家的心跳抖动降至零。

```csharp
private static readonly RecyclableMemoryStreamManager _memoryManager = new RecyclableMemoryStreamManager();

public void SerializePacket(GamePacket packet)
{
    // 使用复用的基于池分配的 MemoryStream
    using var stream = _memoryManager.GetStream("PacketSerialization");
    // do serialize...
}
```

#### 最佳实践: Polly 异常策略处理
外部调用或数据库交互在复杂网络中很容易出现瞬时性异常。
```csharp
using Polly;

// 定义指数退避重试策略: 例如等待 1s -> 2s -> 4s
var retryPolicy = Policy
    .Handle<TimeoutException>()
    .Or<HttpRequestException>()
    .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

// 包装任何容易失败的任务
await retryPolicy.ExecuteAsync(async () =>
{
    await TryConnectToRedisClusterAsync();
});
```

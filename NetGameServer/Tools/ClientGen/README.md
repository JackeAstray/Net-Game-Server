# ClientGen —— Unity / UE 客户端脚本生成器

从 `Protocol/defs/*.def` 生成**可直接导入 Unity / Unreal Engine 的客户端通信脚本**，
实现与 Net-Game-Server 的快速通信。客户端脚本自带一个与服务器 **MemoryPack 二进制格式
逐字节兼容**的零依赖编解码器，无需在客户端引入任何第三方库。

## 用法

```bash
# 生成到 Tools/ClientGen/Output（Unity/ 与 UE/ 两个子目录 + protocol.json 清单）
dotnet run --project NetGameServer/Tools/ClientGen/ClientGen.csproj -c Release -- NetGameServer/Protocol/defs NetGameServer/Tools/ClientGen/Output
```

## 输出

```
Output/
├─ protocol.json        # 协议清单（客户端可见消息 + 结构体，供工具/文档）
├─ Unity/               # 可直接导入 Unity（或任意 C# 工程）
│  ├─ MessageIds.cs     # 消息 ID 常量
│  ├─ MemoryPackCodec.cs# MemoryPack 兼容编解码（MpWriter / MpReader）
│  ├─ Messages.cs       # 消息/结构体类 + Serialize/Deserialize
│  ├─ NetClient.cs      # TCP 客户端（帧封装、Poll 非阻塞接收泵）
│  └─ Demo.cs           # 连接 + 登录示例
└─ UE/                  # 可直接导入 UE（或任意 C++ 工程），零引擎依赖
   ├─ MemoryPack.h      # MemoryPack 兼容编解码（header-only，纯 C++11）
   ├─ Messages.h        # 消息结构体 + Serialize/Deserialize + <Name>MsgId 常量
   ├─ NetClient.h/.cpp  # 标准 TCP 客户端（Winsock / POSIX）
   ├─ Demo.cpp          # 连接 + 登录示例
   └─ README.md         # UE 集成说明
```

## 客户端可见消息的筛选

客户端只与 Gateway 通信，因此：
- 排除 `internal=true`（服务器内部链路，如全部 DB 消息、Center 节点注册、实体迁移）
- 排除 `target=Db`
- 保留 Login(10001-10013) / Game(50001-60003) / Center(30001-30034) / Battle(40001-40106)
- 结构体仅保留被保留消息引用的（按依赖拓扑排序输出）

## 帧格式（与服务器 Gateway 一致）

```
[TotalLength(int32 LE) = 4 + Payload.Length][MsgId(int32 LE)][Payload]
```

Payload 用 MemoryPack 兼容二进制（MpWriter/MpReader，或 C++ mp::Writer/Reader）。
也可直接发送 JSON 文本负载（以 `{` 开头，服务器 `jsonFallback` 自动识别，字段名 PascalCase）。

## MemoryPack 兼容格式（实证）

```
每个类对象（根/直接结构体字段/集合元素/map 值） = 1 字节对象头（成员数）+ 成员数据
bool=1 / int32=4 / int64=8 / float=4 / double=8   （定长小端）
string = int32(~utf8len) + int32(utf16len) + UTF-8；空串 = int32(0)
byte[] = int32(len) + 字节
List/Map = int32(count) + 元素
```

## 验证

`Tests/ClientGenVerify` 把生成的全部 Unity C# 产物编译进来，对每个客户端消息：
用真实 MemoryPack（服务器类）序列化 ↔ 用客户端 codec 反序列化（读方向逐字段一致），
再用客户端 codec 序列化 ↔ 与真实 MemoryPack 字节逐字节一致（写方向）。
`===== 全部验证通过（92 个消息，读写双向逐字节一致）=====`。

C++ 端是同一算法的 1:1 移植（同一份类型模板产出两语言），结构上与已验证的 C# 一致。

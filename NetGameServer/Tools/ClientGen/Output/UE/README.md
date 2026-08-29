# UE 客户端（Net-Game-Server 协议）

零引擎依赖的 C++ 客户端，直接导入 Unreal Engine（或任意 C++ 工程）即可与服务器通信。

## 文件
- `MemoryPack.h`  —— 与服务器 MemoryPack 二进制格式逐字节兼容的编解码器（header-only）
- `Messages.h`    —— 消息结构体 + Serialize/Deserialize + <Name>MsgId 常量（header-only）
- `NetClient.h/.cpp` —— 标准 TCP 客户端（Winsock / POSIX）
- `Demo.cpp`      —— 连接 + 登录示例

## UE 集成步骤
1. 把 `MemoryPack.h`、`Messages.h`、`NetClient.h`、`NetClient.cpp` 拷入你的模块/插件
   （如 `Source/YourGame/Private/Net/`），`Messages.h` 仅依赖 `MemoryPack.h`。
2. 在任意 Actor/组件里持有 `NetClient`，在 `Tick` 中调用 `Poll(OnMessage)`：
   ```cpp
   NetClient Net;
   Net.Connect("127.0.0.1", 31300);          // 建议放后台线程或 BeginPlay
   ClientProtocol::Login L; L.Account = "demo"; L.Password = "demo123";
   Net.Send(ClientProtocol::LoginMsgId, L.Serialize());
   // Tick 里：
   Net.Poll([this](int32_t MsgId, const uint8_t* Data, int32_t Len) {
       if (MsgId == ClientProtocol::LoginResultMsgId) {
           auto R = ClientProtocol::LoginResult::Deserialize(Data, (size_t)Len);
           // R.Success / R.Nickname / R.UserId ...
       }
   });
   ```
3. 也可改用 UE 原生 `FSocket`：帧格式极简（`[TotalLength(4)][MsgId(4)][Payload]`），
   `MemoryPack.h` 的编解码与引擎无关，直接复用。

## 帧格式（与服务器 Gateway 一致）
```
[TotalLength(int32 LE) = 4 + Payload.Length][MsgId(int32 LE)][Payload]
```
Payload = 消息体（MemoryPack 兼容二进制）。也可直接发送 JSON 文本负载
（以 `{` 开头），服务器 `jsonFallback` 自动识别（字段名 PascalCase）。

## 字段类型
bool / int32 / int64 / float / string / bytes / list:T / map<string,string> / 结构体。
与服务器 `Framework.Protocol.Generated`（MemoryPack）逐字节兼容，由 ClientGen 从 `.def` 生成。
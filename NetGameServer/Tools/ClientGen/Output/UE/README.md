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

## 编码要求（重要，否则直接编译不过）
本目录的 `.h/.cpp` 含中文注释，且**已带 UTF-8 BOM** —— 这是必须的：
MSVC 在非 UTF-8 系统代码页（如中文 Windows 的 936/GBK）下会把**无 BOM** 的 UTF-8 源文件按 ANSI 解码，
多字节序列错位后可能吞掉后续代码行，症状是一大片莫名其妙的错误，例如
`error C2039: "ReadCount": 不是 "mp::Reader" 的成员`（而该声明明明就在类内）。
- MSVC：保持文件 BOM 不被剥离即可（**不要**另存为“ANSI/无 BOM”）；或加编译选项 `/utf-8`。
- clang/gcc：通常直接可用（也接受 BOM）；若报错请加 `-finput-charset=UTF-8`。
- 若你的工具链/流水线会剥离 BOM，请显式加 `/utf-8`（MSVC）或 `-finput-charset=UTF-8`。
- 自检脚本：`powershell -File NetGameServer/Tools/ClientGen/verify-ue-syntax.ps1`
  （自动定位 MSVC 并做 `cl /Zs` 语法检查，无工具链时会明确提示跳过）。

## 帧格式（与服务器 Gateway 一致）
```
[TotalLength(int32 LE) = 4 + Payload.Length][MsgId(int32 LE)][Payload]
```
Payload = 消息体（MemoryPack 兼容二进制）。也可直接发送 JSON 文本负载
（以 `{` 开头），服务器 `jsonFallback` 自动识别（字段名 PascalCase）。

## 字段类型
bool / int32 / int64 / float / string / bytes / list:T / map<string,string> / 结构体。
与服务器 `Framework.Protocol.Generated`（MemoryPack）逐字节兼容，由 ClientGen 从 `ProtocolManifest`
（`[GameMessage]`/`[GameStruct]` 编译期产出）生成；旧的 `.def` 解析管线已删除。
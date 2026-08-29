// 客户端示例：连接 Gateway -> 登录 -> 收到 LoginResult
// 编译（示例）：g++ -std=c++17 Demo.cpp NetClient.cpp -o demo -pthread  （Windows 加 -lws2_32）
// UE：把 MemoryPack.h / Messages.h / NetClient.* 放进模块，在 Tick/线程中调用。
#include <chrono>
#include <cstdio>
#include <thread>
#include "NetClient.h"
#include "Messages.h"

static void SleepMillis(int ms) {
    std::this_thread::sleep_for(std::chrono::milliseconds(ms));
}

int main() {
    NetClient client;
    if (!client.Connect("127.0.0.1", 31300)) {
        std::printf("连接失败\n");
        return 1;
    }

    // 登录（MemoryPack 兼容负载）
    ClientProtocol::Login login;
    login.Account = "demo";
    login.Password = "demo123";
    std::vector<uint8_t> payload = login.Serialize();
    client.Send(ClientProtocol::LoginMsgId, payload);

    // 主循环周期性接收（最多 3 秒）
    for (int i = 0; i < 300; ++i) {
        client.Poll([](int32_t msgId, const uint8_t* data, int32_t len) {
            if (msgId == ClientProtocol::LoginResultMsgId) {
                auto r = ClientProtocol::LoginResult::Deserialize(data, (size_t)len);
                std::printf("登录成功=%d 昵称=%s UserId=%d\n", r.Success ? 1 : 0, r.Nickname.c_str(), r.UserId);
            } else {
                std::printf("收到 MsgId=%d 长度=%d\n", msgId, len);
            }
        });
        SleepMillis(10);
    }
    client.Close();
    return 0;
}
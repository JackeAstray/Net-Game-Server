// 客户端示例：连接 Gateway -> 登录 -> 收到 LoginResult
// Unity：把这段逻辑放进 MonoBehaviour，Update() 里调用 client.Poll(OnMessage) 即可。
#nullable enable
using System;
using ClientProtocol;

public static class Demo
{
    public static void Run()
    {
        var client = new NetClient();
        client.Connect("127.0.0.1", 31300);

        // 登录（MemoryPack 兼容负载）
        var login = new Login { Account = "demo", Password = "demo123" };
        client.Send(login);

        // 发送 JSON 负载（服务器 jsonFallback 自动解析，字段名 PascalCase）：
        // client.SendJson(MessageIds.Login, "{\"Account\":\"demo\",\"Password\":\"demo123\"}");

        // 主循环中周期性接收
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            client.Poll(OnMessage);
            System.Threading.Thread.Sleep(10);
        }

        client.Disconnect();

        void OnMessage(int msgId, byte[] payload)
        {
            switch (msgId)
            {
                case MessageIds.LoginResult:
                    var r = LoginResult.Deserialize(payload);
                    Console.WriteLine($"登录成功={r.Success} 昵称={r.Nickname} UserId={r.UserId}");
                    break;
                default:
                    Console.WriteLine($"收到 MsgId={msgId} 长度={payload.Length}");
                    break;
            }
        }
    }
}
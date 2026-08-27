using Framework.Core;

namespace Shared;

/// <summary>
/// 远程日志上报初始化辅助：各服务器启动时调用一次，
/// 从配置读取 LoggerHost/LoggerPort（默认 127.0.0.1:31320），未配置则跳过。
/// </summary>
public static class RemoteLog
{
    private static RemoteLogClient? client;

    /// <summary>
    /// 初始化远程日志上报（幂等）。
    /// </summary>
    /// <param name="nodeId">节点标识，如 "Battle-127.0.0.1:31307"</param>
    public static void Initialize(string nodeId)
    {
        if (client != null) return;

        string? host = ConfigHelper.GetConfig<string>("LoggerHost");
        int port = ConfigHelper.GetConfig<int>("LoggerPort");
        if (string.IsNullOrWhiteSpace(host) || port == 0)
        {
            return; // 未配置日志聚合，跳过（本地日志照常）
        }

        client = new RemoteLogClient(nodeId, host, port);
        client.Start();
    }
}

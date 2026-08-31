namespace Shared;

/// <summary>
/// 节点-中心心跳与重连的共享常量（D4 修复：统一此前散落在各节点的魔法数字 10s/30s，
/// 避免改一处漏一处导致心跳/超时语义分叉）。
/// </summary>
public static class NodeHeartbeatDefaults
{
    /// <summary>节点向 Center 上报心跳/状态的间隔（秒）。</summary>
    public const int HeartbeatIntervalSeconds = 10;

    /// <summary>连接/重连 Center 的超时与健康探测超时（秒）。</summary>
    public const int CenterProbeTimeoutSeconds = 30;
}

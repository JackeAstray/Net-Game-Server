using System.Collections.Concurrent;

namespace Center.Handlers
{
    /// <summary>
    /// Center 节点认证过滤器注册表（P3 加固：内部信任边界）。
    /// CenterServerApp 在节点连接时按 SessionId 登记 InternalAuthFilter，
    /// 注册/状态处理器据此读取握手声明的认证身份（AuthenticatedNodeId），
    /// 校验节点注册/上报身份与握手身份一致，防伪造节点注册/接管。
    /// </summary>
    public static class NodeAuthFilters
    {
        /// <summary>SessionId => 该连接的内部认证过滤器（握手成功后携带 AuthenticatedNodeId）。</summary>
        public static readonly ConcurrentDictionary<long, Framework.Core.Security.InternalAuthFilter> Registry = new();
    }
}

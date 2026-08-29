namespace Framework.Protocol;

/// <summary>
/// 游戏消息基接口。所有 [GameMessage] 声明的消息类都实现此接口
/// （管线由 Framework.Protocol.Generator 源生成器补齐）。
/// </summary>
public interface IGameMessage
{
    /// <summary>消息 ID（由生成代码提供常量 MsgId）</summary>
    int MessageId { get; }

    /// <summary>序列化为 MemoryPack 二进制负载（不含帧头）。</summary>
    byte[] Serialize();
}

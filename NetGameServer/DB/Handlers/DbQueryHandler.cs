using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Shared.Messages.Db;
using Shared;
using DB;
using Shared.Data.Social;
namespace DB.Handlers
{
    /// <summary>
    /// 处理数据库查询相关操作的处理器类。
    /// </summary>
    public partial class DbQueryHandler
    {
        /// <summary>
        /// 账号级串行（P1-2 丢失更新修复）：同一用户/账号的读-改-写请求严格按提交顺序执行，
        /// 防止并发写同一用户行时相互覆盖（登录计数、在线状态、密码、好友/黑名单列表），
        /// 也保证同一用户的读请求能读到先前已排队的写结果（read-your-writes）。
        /// 不同 key 之间并发执行（固定 worker 池 ≈ CPU 核数，覆盖 PBKDF2 这类 CPU 密集段的并行度）；
        /// 仅用户/账号维度的读写请求入队，全局只读查询（GetMaxUid/OnlineStats/AccountQuery 等）不排队。
        /// </summary>
        private static readonly Framework.Core.OrderedTaskQueue perUserQueue =
            new("DbPerUser", maxConcurrency: Environment.ProcessorCount);

        private static string AccountKey(string account) => "A:" + account;

        private static string UserKey(long userId) => "U:" + userId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>按用户/账号键串行执行一次 DB 读写（异常由队列内部捕获记录，不向上抛）。</summary>
        private static Task RunPerUser(object key, Func<Task> work) => perUserQueue.EnqueueAsync(key, work);

        // P1 修复：按类型缓存响应属性反射结果，避免每个响应都调用 GetProperty（响应量大时反射开销不可忽略）。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (System.Reflection.PropertyInfo? Success, System.Reflection.PropertyInfo? Message)>
            ResponsePropCache = new();

        /// <summary>V6 修复：处理器失败时统一回一个失败响应（成功/失败都回包，避免调用方按 RequestId 等待时永久挂起）。
        /// RequestContextSession.Send 会自动附加 RequestId，调用方即可关联到原请求。</summary>
        /// <param name="session">要发送的目标会话（RequestContextSession 或其包装）。</param>
        /// <param name="msgId">响应消息 ID。</param>
        /// <param name="message">失败原因文本。</param>
        private static void SendFailureResponse(ISession session, int msgId, string message)
        {
            try
            {
                byte[] payload = Shared.Json.SerializeToUtf8Bytes(new { Success = false, Message = message });
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payload, out int totalLength);
                Network.PacketSender.Send(session, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"发送 DB 失败响应异常 MsgId:{msgId} Exception:{ex}");
            }
        }

        /// <summary>
        /// 将响应对象序列化为 UTF-8 JSON，按 msgId 前缀封包并通过会话发送；异常被捕获并记录。
        /// </summary>
        /// <remarks>包格式：4 字节小端 msgId 后跟 JSON 负载（若提供则包含附加的请求 ID 元数据）。使用 Shared.Json.SerializeToUtf8Bytes
        /// 进行序列化并通过会话发送；内部捕获并记录异常。</remarks>
        /// <typeparam name="T">响应类型，可序列化为 JSON；若包含 bool Success 与 Message 属性，在 Success 为 false 时记录警告。</typeparam>
        /// <param name="session">用于发送封装后数据包的会话。</param>
        /// <param name="msgId">消息标识，以 4 字节小端格式写入包头。</param>
        /// <param name="response">要发送的响应对象，序列化为 UTF-8 JSON；可包含 Success (bool) 和 Message 属性用于日志。</param>
        /// <param name="requestId">可选请求标识，会附加到负载元数据用于路由/关联。</param>
        private static void SendDbResponse<T>(ISession session, int msgId, T response, long? requestId = null)
        {
            try
            {
                if (response != null)
                {
                    var props = ResponsePropCache.GetOrAdd(typeof(T), t =>
                    {
                        var success = t.GetProperty("Success");
                        var message = t.GetProperty("Message");
                        return (success, message);
                    });
                    if (props.Success?.PropertyType == typeof(bool) && props.Success.GetValue(response) is bool success && !success)
                    {
                        string message = props.Message?.GetValue(response)?.ToString() ?? string.Empty;
                        Log.Warning($"DB 响应失败 MsgId:{msgId} RequestId:{requestId?.ToString() ?? "none"} Message:{message}");
                    }
                }

                byte[] payload = Shared.Json.SerializeToUtf8Bytes(response!);
                if (requestId.HasValue)
                {
                    payload = Shared.RouteMetadata.AttachRequestId(payload, requestId.Value);
                }

                // 帧长度修复（P1）：统一用 BuildPacket 加长度头 + PacketSender 免启发式发送，
                // 避免裸 [MsgId][payload] 触发 TcpSession.Send 的长度启发式误判（MsgId 恰等于负载长度时漏加前缀导致对端流错位）。
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payload, out int totalLength);
                Network.PacketSender.Send(session, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"发送 DB 响应失败 MsgId:{msgId} RequestId:{requestId?.ToString() ?? "none"} Exception:{ex}");
            }
        }
    }
}

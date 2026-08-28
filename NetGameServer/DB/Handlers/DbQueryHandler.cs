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
                    var successProperty = typeof(T).GetProperty("Success");
                    if (successProperty?.PropertyType == typeof(bool) && successProperty.GetValue(response) is bool success && !success)
                    {
                        string message = typeof(T).GetProperty("Message")?.GetValue(response)?.ToString() ?? string.Empty;
                        Log.Warning($"DB 响应失败 MsgId:{msgId} RequestId:{requestId?.ToString() ?? "none"} Message:{message}");
                    }
                }

                byte[] payload = Shared.Json.SerializeToUtf8Bytes(response);
                if (requestId.HasValue)
                {
                    payload = Shared.RouteMetadata.AttachRequestId(payload, requestId.Value);
                }

                byte[] packet = new byte[payload.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), msgId);
                payload.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            }
            catch (Exception ex)
            {
                Log.Error($"发送 DB 响应失败 MsgId:{msgId} RequestId:{requestId?.ToString() ?? "none"} Exception:{ex}");
            }
        }
    }
}

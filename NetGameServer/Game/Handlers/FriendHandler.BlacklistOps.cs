using System;
using System.Collections.Concurrent;
using Game.Network;
using Shared;
using Shared.Messages;
using Shared.Messages.Social;
using Game.Managers;
using Network.Routing;
namespace Game.Handlers
{
    /// <summary>
    /// 好友系统 —— 黑名单模块（添加/移除/列表 + 黑名单缓存/封禁判断）。
    /// 与 FriendHandler.cs 同属一个 partial class，按业务域分文件组织（对标 KBE 按逻辑拆分）。
    /// </summary>
    public static partial class FriendHandler
    {
        /// <summary>
        /// 处理添加黑名单请求：验证会话与负载，检查登录与数据库连接，验证目标 UniqueId，并将数据库请求转发或返回失败响应。
        /// </summary>
        /// <remarks>在必要时通过 SendSimpleResponse 发送失败响应；成功时构造 DbAddBlacklistRequest 并使用 TrySendDbRequest 转发至
        /// DB 服务；通过 PlayerSessionManager 获取用户标识。</remarks>
        /// <param name="sessionBase">会话基对象，预期为 ClientSessionWrapper 实例；若不是则忽略请求。</param>
        /// <param name="payload">包含序列化的 AddBlacklistRequest 的 UTF-8 字节负载。</param>
        internal static void HandleAddBlacklistRequest(ClientSessionWrapper session, AddBlacklistRequest? req)
        {
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.AddBlacklistRes, new AddBlacklistResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.AddBlacklistRes, new AddBlacklistResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.AddBlacklistRes, new AddBlacklistResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.TargetUniqueId))
            {
                SendSimpleResponse(session, MessageIds.AddBlacklistRes, new AddBlacklistResponse { Success = false, Message = "目标UniqueId不能为空" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbAddBlacklistRequest
            {
                UserId = userId,
                TargetUniqueId = req.TargetUniqueId.Trim()
            };

            if (!TrySendDbRequest(MessageIds.DbAddBlacklistReq, session, dbReq, session.SessionId, MessageIds.AddBlacklistRes))
            {
                SendSimpleResponse(session, MessageIds.AddBlacklistRes, new AddBlacklistResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        /// <summary>
        /// 处理客户端的移除黑名单请求：反序列化请求、验证会话与参数，并将操作转发到数据库服务或返回错误响应。
        /// </summary>
        /// <remarks>在会话未登录、请求格式不合法、目标 UniqueId 为空或数据库服务不可用时发送失败响应；成功时向数据库发送
        /// DbRemoveBlacklistRequest。</remarks>
        /// <param name="sessionBase">网络会话基对象，预期为 ClientSessionWrapper；用于获取会话 ID 并向客户端发送响应。</param>
        /// <param name="payload">包含 RemoveBlacklistRequest 的 UTF-8 编码序列化字节数据。</param>
        internal static void HandleRemoveBlacklistRequest(ClientSessionWrapper session, RemoveBlacklistRequest? req)
        {
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.RemoveBlacklistRes, new RemoveBlacklistResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.RemoveBlacklistRes, new RemoveBlacklistResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.RemoveBlacklistRes, new RemoveBlacklistResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.TargetUniqueId))
            {
                SendSimpleResponse(session, MessageIds.RemoveBlacklistRes, new RemoveBlacklistResponse { Success = false, Message = "目标UniqueId不能为空" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbRemoveBlacklistRequest
            {
                UserId = userId,
                TargetUniqueId = req.TargetUniqueId.Trim()
            };

            if (!TrySendDbRequest(MessageIds.DbRemoveBlacklistReq, session, dbReq, session.SessionId, MessageIds.RemoveBlacklistRes))
            {
                SendSimpleResponse(session, MessageIds.RemoveBlacklistRes, new RemoveBlacklistResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        /// <summary>
        /// 处理获取黑名单请求：验证会话、反序列化请求并将数据库查询请求转发到数据库服务。
        /// </summary>
        /// <remarks>在请求格式无效、会话未登录或数据库服务未连接时发送失败响应；成功时向数据库发送
        /// DbGetBlacklistRequest，并在发送失败时返回错误响应。</remarks>
        /// <param name="sessionBase">会话实例，期望为 ClientSessionWrapper；若不是则忽略请求。</param>
        /// <param name="payload">包含请求的 UTF-8 JSON 字节数据，反序列化为 GetBlacklistRequest。</param>
        internal static void HandleGetBlacklistRequest(ClientSessionWrapper session, GetBlacklistRequest? req)
        {
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.GetBlacklistRes, new GetBlacklistResponse { Success = false, Message = "请求格式无效", Blacklists = Array.Empty<BlacklistInfo>() });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.GetBlacklistRes, new GetBlacklistResponse { Success = false, Message = "会话未登录或未绑定", Blacklists = Array.Empty<BlacklistInfo>() });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.GetBlacklistRes, new GetBlacklistResponse { Success = false, Message = "DB服务未连接", Blacklists = Array.Empty<BlacklistInfo>() });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbGetBlacklistRequest
            {
                UserId = userId
            };

            if (!TrySendDbRequest(MessageIds.DbGetBlacklistReq, session, dbReq, session.SessionId, MessageIds.GetBlacklistRes))
            {
                SendSimpleResponse(session, MessageIds.GetBlacklistRes, new GetBlacklistResponse { Success = false, Message = "发送DB请求失败", Blacklists = Array.Empty<BlacklistInfo>() });
            }
        }

        /// <summary>
        /// 确定发送者是否被指定目标用户屏蔽。
        /// </summary>
        /// <remarks>依赖 BlacklistCache 的内容，假定被屏蔽用户以键集合表示。匹配前要求两个 ID 均为正数。</remarks>
        /// <param name="targetUserId">目标用户的 ID，应为正整数，用于在黑名单缓存中查找。</param>
        /// <param name="senderUserId">发送者用户的 ID，应为正整数，用于在目标用户的黑名单中查找。</param>
        /// <returns>若目标用户的黑名单包含发送者用户则返回 true；否则返回 false。</returns>
        public static bool IsBlockedByTarget(int targetUserId, int senderUserId)
        {
            return targetUserId > 0
                && senderUserId > 0
                && BlacklistCache.TryGetValue(targetUserId, out var blockedUsers)
                && blockedUsers.ContainsKey(senderUserId);
        }

        /// <summary>
        /// 将指定的被阻止用户添加到指定阻止者的黑名单缓存。
        /// </summary>
        /// <remarks>如果任一标识小于等于 0，则不执行任何操作。为阻止者在 BlacklistCache 中创建或获取 ConcurrentDictionary，并将被阻止用户的键设置为
        /// 0。</remarks>
        /// <param name="blockerUserId">阻止者的用户标识；必须大于 0。</param>
        /// <param name="blockedUserId">被阻止者的用户标识；必须大于 0。</param>
        private static void AddBlacklistCache(int blockerUserId, int blockedUserId)
        {
            if (blockerUserId <= 0 || blockedUserId <= 0)
            {
                return;
            }

            var blockedUsers = BlacklistCache.GetOrAdd(blockerUserId, _ => new ConcurrentDictionary<int, byte>());
            blockedUsers[blockedUserId] = 0;
        }

        /// <summary>
        /// 从黑名单缓存中移除指定屏蔽者对指定用户的屏蔽记录。
        /// </summary>
        /// <remarks>若任一 ID 非正或缓存中不存在相应条目，则不进行任何操作。</remarks>
        /// <param name="blockerUserId">屏蔽者的用户 ID；应为正整数。</param>
        /// <param name="blockedUserId">被屏蔽用户的用户 ID；应为正整数。</param>
        private static void RemoveBlacklistCache(int blockerUserId, int blockedUserId)
        {
            if (blockerUserId <= 0 || blockedUserId <= 0)
            {
                return;
            }

            if (BlacklistCache.TryGetValue(blockerUserId, out var blockedUsers))
            {
                blockedUsers.TryRemove(blockedUserId, out _);
            }
        }

        /// <summary>
        /// 为指定封锁者设置黑名单缓存。将有效的被封锁用户 ID 存入并发字典并赋值到全局 BlacklistCache。
        /// </summary>
        /// <remarks>使用 ConcurrentDictionary 以 byte 作为占位值存储被封锁的用户 ID，并替换或新增 BlacklistCache
        /// 中对应的条目。赋值为一次性替换操作；对 BlacklistCache 的外部并发访问需按需同步。</remarks>
        /// <param name="blockerUserId">封锁者的用户 ID；若小于或等于 0 则不做任何操作。</param>
        /// <param name="blacklists">要缓存的 BlacklistInfo 数组；遍历并将每个 BlockedUserId 大于 0 的条目加入缓存。若为 null 则生成空的并发字典。</param>
        private static void SetBlacklistCache(int blockerUserId, BlacklistInfo[] blacklists)
        {
            if (blockerUserId <= 0)
            {
                return;
            }

            var blockedUsers = new ConcurrentDictionary<int, byte>();
            if (blacklists != null)
            {
                foreach (var item in blacklists)
                {
                    if (item.BlockedUserId > 0)
                    {
                        blockedUsers[item.BlockedUserId] = 0;
                    }
                }
            }

            BlacklistCache[blockerUserId] = blockedUsers;
        }
    }
}

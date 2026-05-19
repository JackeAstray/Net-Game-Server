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
    /// 好友系统处理器，负责处理好友相关的请求，如添加好友、删除好友、设置备注、获取好友列表以及邀请游戏等。
    /// </summary>
    public static class FriendHandler
    {
        private static readonly ConcurrentDictionary<long, PendingFriendRequest> PendingFriendRequests = new();
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> BlacklistCache = new();
        private static long requestIdSeed = DateTime.UtcNow.Ticks;

        private sealed class PendingFriendRequest
        {
            public long SessionId { get; set; }
            public int ResponseMsgId { get; set; }
            public bool IsInviteResolve { get; set; }
            public bool IsInviteSenderResolve { get; set; }
            public int InviteRoomId { get; set; }
            public int InviteTargetUserId { get; set; }
        }

        /// <summary>
        /// 向指定的 MessageRouter 注册好友与黑名单相关的消息处理器。
        /// </summary>
        ///
        /// <remarks>注册以下消息标识符的处理器：MessageIds.AddFriendReq、MessageIds.RemoveFriendReq、MessageIds.SetFriendRemarkReq、MessageIds.GetFriendsReq、MessageIds.InviteGameReq、MessageIds.AddBlacklistReq、MessageIds.RemoveBlacklistReq、MessageIds.GetBlacklistReq。</remarks>
        /// <param name="router">用于注册消息处理器的 MessageRouter 实例。</param>
        public static void Register(MessageRouter router)
        {
            router.RegisterHandler(MessageIds.AddFriendReq, (s, p) => HandleAddFriendRequest(s, p));
            router.RegisterHandler(MessageIds.RemoveFriendReq, (s, p) => HandleRemoveFriendRequest(s, p));
            router.RegisterHandler(MessageIds.SetFriendRemarkReq, (s, p) => HandleSetFriendRemarkRequest(s, p));
            router.RegisterHandler(MessageIds.GetFriendsReq, (s, p) => HandleGetFriendsRequest(s, p));
            router.RegisterHandler(MessageIds.InviteGameReq, (s, p) => HandleInviteGameRequest(s, p));
            router.RegisterHandler(MessageIds.AddBlacklistReq, (s, p) => HandleAddBlacklistRequest(s, p));
            router.RegisterHandler(MessageIds.RemoveBlacklistReq, (s, p) => HandleRemoveBlacklistRequest(s, p));
            router.RegisterHandler(MessageIds.GetBlacklistReq, (s, p) => HandleGetBlacklistRequest(s, p));
        }

        /// <summary>
        /// 处理添加好友请求，接收客户端发送的添加好友请求，解析请求内容，并将请求转发给数据库进行处理。处理完成后，向客户端发送响应结果。
        /// </summary>
        /// <param name="sessionBase">当前的网络会话。</param>
        /// <param name="payload">客户端发送的请求数据。</param>
        private static void HandleAddFriendRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<AddFriendRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.AddFriendRes, new AddFriendResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.AddFriendRes, new AddFriendResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.AddFriendRes, new AddFriendResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.TargetUniqueId))
            {
                SendSimpleResponse(session, MessageIds.AddFriendRes, new AddFriendResponse { Success = false, Message = "目标UniqueId不能为空" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbAddFriendRequest
            {
                UserId = (int)userId,
                FriendUniqueId = req.TargetUniqueId.Trim(),
                Remark = req.Remark
            };

            if (!TrySendDbRequest(MessageIds.DbAddFriendReq, dbReq, session.SessionId, MessageIds.AddFriendRes))
            {
                SendSimpleResponse(session, MessageIds.AddFriendRes, new AddFriendResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        /// <summary>
        /// 处理来自客户端的删除好友请求：验证会话与请求格式，检查登录状态和数据库连接，构建并转发删除好友的数据库请求；在失败时返回相应的错误响应。
        /// </summary>
        /// <remarks>方法通过发送消息与数据库服务交互并向客户端发送响应；在会话未绑定、请求无效或 DB 未连接时返回错误响应。</remarks>
        /// <param name="sessionBase">会话对象；应为 ClientSessionWrapper，用于获取会话标识并发送响应。</param>
        /// <param name="payload">包含请求的 UTF-8 JSON 负载，用于反序列化为 RemoveFriendRequest。</param>
        private static void HandleRemoveFriendRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<RemoveFriendRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.RemoveFriendRes, new RemoveFriendResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.RemoveFriendRes, new RemoveFriendResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.RemoveFriendRes, new RemoveFriendResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.FriendUniqueId))
            {
                SendSimpleResponse(session, MessageIds.RemoveFriendRes, new RemoveFriendResponse { Success = false, Message = "好友UniqueId不能为空" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbRemoveFriendRequest
            {
                UserId = (int)userId,
                FriendUniqueId = req.FriendUniqueId.Trim()
            };

            if (!TrySendDbRequest(MessageIds.DbRemoveFriendReq, dbReq, session.SessionId, MessageIds.RemoveFriendRes))
            {
                SendSimpleResponse(session, MessageIds.RemoveFriendRes, new RemoveFriendResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        /// <summary>
        /// 处理设置好友备注请求：反序列化请求数据，验证会话和参数，检查数据库连接，构建并转发数据库请求；出错时发送失败响应。
        /// </summary>
        /// <remarks>在请求格式无效、会话未登录或未绑定、数据库未连接或 FriendUniqueId 为空时发送 SetFriendRemarkRes 的失败响应。构建
        /// DbSetFriendRemarkRequest（包含 UserId、FriendUniqueId（已修剪）和 Remark）并通过 TrySendDbRequest 转发为
        /// DbSetFriendRemarkReq；若发送失败则返回失败响应。</remarks>
        /// <param name="sessionBase">会话接口实例，期望为 ClientSessionWrapper 类型；若非该类型则忽略请求。</param>
        /// <param name="payload">包含 UTF-8 编码的 JSON 请求数据，反序列化为 SetFriendRemarkRequest。</param>
        private static void HandleSetFriendRemarkRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<SetFriendRemarkRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.SetFriendRemarkRes, new SetFriendRemarkResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.SetFriendRemarkRes, new SetFriendRemarkResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.SetFriendRemarkRes, new SetFriendRemarkResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.FriendUniqueId))
            {
                SendSimpleResponse(session, MessageIds.SetFriendRemarkRes, new SetFriendRemarkResponse { Success = false, Message = "好友UniqueId不能为空" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbSetFriendRemarkRequest
            {
                UserId = (int)userId,
                FriendUniqueId = req.FriendUniqueId.Trim(),
                Remark = req.Remark
            };

            if (!TrySendDbRequest(MessageIds.DbSetFriendRemarkReq, dbReq, session.SessionId, MessageIds.SetFriendRemarkRes))
            {
                SendSimpleResponse(session, MessageIds.SetFriendRemarkRes, new SetFriendRemarkResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        /// <summary>
        /// 处理获取好友列表的请求：验证会话类型并反序列化请求负载，检查会话登录状态和数据库连接，必要时向数据库服务转发获取好友请求或返回失败响应。
        /// </summary>
        /// <remarks>在请求格式无效、会话未登录或数据库服务不可用时发送相应的失败响应；在验证通过且数据库可用时构造 DbGetFriendsRequest
        /// 并尝试发送到数据库服务。</remarks>
        /// <param name="sessionBase">客户端的网络会话基对象（Network.ISession），方法会将其转换为 ClientSessionWrapper 以继续处理。</param>
        /// <param name="payload">只读的字节内存，包含 JSON 编码的 GetFriendsRequest，方法从中反序列化请求数据。</param>
        private static void HandleGetFriendsRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<GetFriendsRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.GetFriendsRes, new GetFriendsResponse { Success = false, Message = "请求格式无效", Friends = Array.Empty<FriendInfo>() });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.GetFriendsRes, new GetFriendsResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.GetFriendsRes, new GetFriendsResponse { Success = false, Message = "DB服务未连接", Friends = Array.Empty<FriendInfo>() });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbGetFriendsRequest
            {
                UserId = (int)userId
            };

            if (!TrySendDbRequest(MessageIds.DbGetFriendsReq, dbReq, session.SessionId, MessageIds.GetFriendsRes))
            {
                SendSimpleResponse(session, MessageIds.GetFriendsRes, new GetFriendsResponse { Success = false, Message = "发送DB请求失败", Friends = Array.Empty<FriendInfo>() });
            }
        }

        /// <summary>
        /// 处理客户端的邀请游戏请求：验证会话与负载，解析 InviteGameRequest，校验好友 UniqueId，向 DB 请求解析好友并设置待处理项，必要时向客户端返回失败响应。
        /// </summary>
        /// <remarks>在校验失败时会向客户端发送失败响应。若校验通过，会向 DB 服务发送 DbResolveUserByUniqueId 请求并在 pending 中设置
        /// IsInviteResolve 与 InviteRoomId。依赖 PlayerSessionManager 和 GameServerApp.DbClient。</remarks>
        /// <param name="sessionBase">客户端会话基类（期望为 ClientSessionWrapper），用于发送响应并获取会话标识。</param>
        /// <param name="payload">请求负载的只读字节内存，反序列化为 InviteGameRequest。</param>
        private static void HandleInviteGameRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<InviteGameRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.FriendUniqueId))
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "好友UniqueId不能为空" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbResolveUserByUniqueIdRequest
            {
                UniqueId = req.FriendUniqueId.Trim()
            };

            if (!TrySendDbRequest(MessageIds.DbResolveUserByUniqueIdReq, dbReq, session.SessionId, MessageIds.InviteGameRes, pending =>
            {
                pending.IsInviteResolve = true;
                pending.InviteRoomId = req.RoomId;
            }))
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        /// <summary>
        /// 处理添加黑名单请求：验证会话与负载，检查登录与数据库连接，验证目标 UniqueId，并将数据库请求转发或返回失败响应。
        /// </summary>
        /// <remarks>在必要时通过 SendSimpleResponse 发送失败响应；成功时构造 DbAddBlacklistRequest 并使用 TrySendDbRequest 转发至
        /// DB 服务；通过 PlayerSessionManager 获取用户标识。</remarks>
        /// <param name="sessionBase">会话基对象，预期为 ClientSessionWrapper 实例；若不是则忽略请求。</param>
        /// <param name="payload">包含序列化的 AddBlacklistRequest 的 UTF-8 字节负载。</param>
        private static void HandleAddBlacklistRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<AddBlacklistRequest>(payload.Span);
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

            if (!TrySendDbRequest(MessageIds.DbAddBlacklistReq, dbReq, session.SessionId, MessageIds.AddBlacklistRes))
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
        private static void HandleRemoveBlacklistRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<RemoveBlacklistRequest>(payload.Span);
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

            if (!TrySendDbRequest(MessageIds.DbRemoveBlacklistReq, dbReq, session.SessionId, MessageIds.RemoveBlacklistRes))
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
        private static void HandleGetBlacklistRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<GetBlacklistRequest>(payload.Span);
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

            if (!TrySendDbRequest(MessageIds.DbGetBlacklistReq, dbReq, session.SessionId, MessageIds.GetBlacklistRes))
            {
                SendSimpleResponse(session, MessageIds.GetBlacklistRes, new GetBlacklistResponse { Success = false, Message = "发送DB请求失败", Blacklists = Array.Empty<BlacklistInfo>() });
            }
        }

        /// <summary>
        /// 将序列化的请求发送到数据库客户端并注册等待响应的条目。
        /// </summary>
        /// <remarks>为请求生成唯一 RequestId，将请求序列化为 UTF-8 并附加路由元数据，再构建并发送数据包；发送失败时会从 PendingFriendRequests
        /// 中移除已注册项；无论成功与否都会将用于构建数据包的数组归还给 ArrayPool。</remarks>
        /// <typeparam name="TRequest">要发送到数据库并序列化的请求类型。</typeparam>
        /// <param name="dbMsgId">数据库端接收消息的消息 ID。</param>
        /// <param name="request">要序列化并发送的请求对象。</param>
        /// <param name="clientSessionId">客户端会话 ID，用于在响应到达时路由回该会话。</param>
        /// <param name="responseMsgId">期望接收的响应消息 ID，用于构建待处理项。</param>
        /// <param name="configurePending">可选操作，用于在注册待处理响应前配置 PendingFriendRequest 的额外字段。</param>
        /// <returns>若成功将请求发送并注册为待处理响应则返回 true；若发生错误或数据库客户端为空则返回 false。</returns>
        private static bool TrySendDbRequest<TRequest>(int dbMsgId, TRequest request, long clientSessionId, int responseMsgId, Action<PendingFriendRequest>? configurePending = null)
        {
            var dbClient = GameServerApp.DbClient;
            if (dbClient == null)
            {
                Shared.Log.Error($"Game 向 DB 发送请求失败：DB 连接为空 MsgId:{dbMsgId} SessionId:{clientSessionId}");
                return false;
            }

            long requestId = System.Threading.Interlocked.Increment(ref requestIdSeed);
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(request);
            byte[] routedPayload = Shared.RouteMetadata.AttachRequestId(payload, requestId);
            byte[] packet = PacketBuilder.BuildPacket(dbMsgId, routedPayload, out int totalLength);

            try
            {
                var pending = new PendingFriendRequest
                {
                    SessionId = clientSessionId,
                    ResponseMsgId = responseMsgId
                };
                configurePending?.Invoke(pending);
                PendingFriendRequests[requestId] = pending;
                dbClient.Send(packet.AsSpan(0, totalLength).ToArray());
                return true;
            }
            catch (Exception ex)
            {
                PendingFriendRequests.TryRemove(requestId, out _);
                Shared.Log.Error($"Game 发送 DB 请求失败 MsgId:{dbMsgId} SessionId:{clientSessionId} RequestId:{requestId} Exception:{ex}");
                return false;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        /// <summary>
        /// 根据数据库回包的消息标识解析并处理响应，向匹配的会话发送响应或通知，必要时更新缓存并触发后续 DB 请求。
        /// </summary>
        /// <remarks>根据载荷中的 RequestId 匹配待处理项，匹配失败或载荷缺少 RequestId 时记录警告并返回 false。对不同 dbMsgId
        /// 执行相应的反序列化、发送会话响应或通知、更新好友/黑名单缓存，并在邀请流程中可能发起后续 DB 请求；该方法会移除已处理的待处理请求并调用 PlayerSessionManager
        /// 与相关缓存操作。</remarks>
        /// <param name="gameSession">用于发送响应、通知和转发数据的当前网络会话。</param>
        /// <param name="dbMsgId">数据库返回的消息标识，用于选择对应的解析与处理逻辑。</param>
        /// <param name="payload">包含路由元数据和序列化响应体的原始只读字节序列。</param>
        /// <returns>已成功识别并处理该 DB 回包则返回 true；未处理或匹配失败则返回 false。</returns>
        public static bool TryHandleDbResponse(global::Network.ISession gameSession, int dbMsgId, ReadOnlyMemory<byte> payload)
        {
            if (!Shared.RouteMetadata.TryExtractRequestId(payload, out long requestId, out var cleanPayload))
            {
                Shared.Log.Warning($"Game 收到缺少 RequestId 的 DB 回包 MsgId:{dbMsgId}");
                return false;
            }

            if (!PendingFriendRequests.TryRemove(requestId, out var pending))
            {
                Shared.Log.Warning($"Game 未找到匹配的待处理 DB 请求 RequestId:{requestId} MsgId:{dbMsgId}");
                return false;
            }

            int requesterUserId = PlayerSessionManager.Instance.GetUserIdBySessionId(pending.SessionId);

            switch (dbMsgId)
            {
                case MessageIds.DbAddFriendRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbAddFriendResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析添加好友 DB 回包失败 RequestId:{requestId}");
                        }
                        var res = new AddFriendResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "添加好友失败"
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbRemoveFriendRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbRemoveFriendResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析删除好友 DB 回包失败 RequestId:{requestId}");
                        }
                        var res = new RemoveFriendResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "删除好友失败"
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbSetFriendRemarkRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbSetFriendRemarkResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析设置备注 DB 回包失败 RequestId:{requestId}");
                        }
                        var res = new SetFriendRemarkResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "设置备注失败"
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbGetFriendsRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbGetFriendsResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析获取好友列表 DB 回包失败 RequestId:{requestId}");
                        }
                        var friends = dbRes?.Friends == null
                            ? Array.Empty<FriendInfo>()
                            : dbRes.Friends.ConvertAll(f => new FriendInfo
                            {
                                FriendUserId = f.FriendUserId,
                                FriendUniqueId = f.FriendUniqueId,
                                Nickname = f.FriendNickname,
                                Remark = f.Remark,
                                IsOnline = PlayerSessionManager.Instance.GetSessionIdByUserId(f.FriendUserId) > 0
                            }).ToArray();

                        var res = new GetFriendsResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "获取好友列表失败",
                            Friends = friends
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbAddBlacklistRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbAddBlacklistResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析添加黑名单 DB 回包失败 RequestId:{requestId}");
                        }
                        if (dbRes?.Success == true && requesterUserId > 0 && dbRes.TargetUserId > 0)
                        {
                            AddBlacklistCache(requesterUserId, dbRes.TargetUserId);
                        }

                        var res = new AddBlacklistResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "添加黑名单失败"
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbRemoveBlacklistRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbRemoveBlacklistResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析移除黑名单 DB 回包失败 RequestId:{requestId}");
                        }
                        if (dbRes?.Success == true && requesterUserId > 0 && dbRes.TargetUserId > 0)
                        {
                            RemoveBlacklistCache(requesterUserId, dbRes.TargetUserId);
                        }

                        var res = new RemoveBlacklistResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "移除黑名单失败"
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbGetBlacklistRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbGetBlacklistResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析获取黑名单 DB 回包失败 RequestId:{requestId}");
                        }
                        var blacklists = dbRes?.Blacklists == null
                            ? Array.Empty<BlacklistInfo>()
                            : dbRes.Blacklists.ConvertAll(b => new BlacklistInfo
                            {
                                BlockedUserId = b.BlockedUserId,
                                BlockedUniqueId = b.BlockedUniqueId,
                                BlockedNickname = b.BlockedNickname,
                                AddTime = b.AddTime
                            }).ToArray();

                        if (dbRes?.Success == true && requesterUserId > 0)
                        {
                            SetBlacklistCache(requesterUserId, blacklists);
                        }

                        var res = new GetBlacklistResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "获取黑名单失败",
                            Blacklists = blacklists
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbResolveUserByUniqueIdRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbResolveUserByUniqueIdResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析按 UniqueId 解析用户 DB 回包失败 RequestId:{requestId}");
                        }
                        if (!pending.IsInviteResolve)
                        {
                            return false;
                        }

                        var inviteRes = new InviteGameResponse { Success = false, Message = "不在线或无法邀请" };

                        if (requesterUserId <= 0)
                        {
                            inviteRes.Message = "会话未登录或未绑定";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                            return true;
                        }

                        if (dbRes?.Success != true || dbRes.UserId <= 0)
                        {
                            inviteRes.Message = dbRes?.Message ?? "目标用户不存在";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                            return true;
                        }

                        if (dbRes.UserId == requesterUserId)
                        {
                            inviteRes.Message = "不能邀请自己";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                            return true;
                        }

                        if (IsBlockedByTarget(dbRes.UserId, requesterUserId))
                        {
                            inviteRes.Message = "对方已将你拉黑";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                            return true;
                        }

                        var senderResolveReq = new Shared.Messages.Db.DbResolveUserByUserIdRequest
                        {
                            UserId = requesterUserId
                        };

                        bool sent = TrySendDbRequest(MessageIds.DbResolveUserByUserIdReq, senderResolveReq, pending.SessionId, pending.ResponseMsgId, nextPending =>
                        {
                            nextPending.IsInviteSenderResolve = true;
                            nextPending.InviteRoomId = pending.InviteRoomId;
                            nextPending.InviteTargetUserId = dbRes.UserId;
                        });

                        if (!sent)
                        {
                            inviteRes.Message = "发送DB请求失败";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                        }

                        return true;
                    }
                case MessageIds.DbResolveUserByUserIdRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbResolveUserByUserIdResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析按 UserId 解析用户 DB 回包失败 RequestId:{requestId}");
                        }
                        if (!pending.IsInviteSenderResolve)
                        {
                            return false;
                        }

                        var inviteRes = new InviteGameResponse { Success = false, Message = "不在线或无法邀请" };
                        if (requesterUserId <= 0)
                        {
                            inviteRes.Message = "会话未登录或未绑定";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                            return true;
                        }

                        string inviterUniqueId = dbRes?.Success == true ? (dbRes.UniqueId ?? string.Empty) : string.Empty;
                        string inviterNickname = dbRes?.Success == true ? (dbRes.Nickname ?? string.Empty) : string.Empty;

                        long targetSessionId = PlayerSessionManager.Instance.GetSessionIdByUserId(pending.InviteTargetUserId);
                        if (targetSessionId > 0)
                        {
                            var notif = new InviteGameNotification
                            {
                                InviterUniqueId = inviterUniqueId,
                                InviterNickname = string.IsNullOrWhiteSpace(inviterNickname) ? $"Player_{requesterUserId}" : inviterNickname,
                                RoomId = pending.InviteRoomId
                            };
                            var notifPayload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(notif), targetSessionId);
                            var notifData = PacketBuilder.BuildPacket(MessageIds.InviteGameNotif, notifPayload, out int notifLength);
                            try
                            {
                                gameSession.Send(notifData.AsSpan(0, notifLength).ToArray());
                            }
                            finally
                            {
                                System.Buffers.ArrayPool<byte>.Shared.Return(notifData);
                            }

                            inviteRes.Success = true;
                            inviteRes.Message = "邀请已发送";
                        }

                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                        return true;
                    }
                default:
                    return false;
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

        /// <summary>
        /// 将响应序列化为 UTF-8 字节、附加目标会话 ID、构建数据包并通过指定会话发送。
        /// </summary>
        /// <remarks>序列化使用 Shared.Json.SerializeToUtf8Bytes，负载中附加路由元数据；数据包由 PacketBuilder.BuildPacket
        /// 构建，发送完成后在 finally 块将用于构建的字节数组归还至 ArrayPool<byte>.Shared。</remarks>
        /// <typeparam name="T">响应对象的类型。</typeparam>
        /// <param name="gameSession">用于发送数据包的会话实例。</param>
        /// <param name="sessionId">目标会话的唯一标识符（会话 ID）。</param>
        /// <param name="msgId">数据包的消息标识符。</param>
        /// <param name="response">要序列化并发送的响应对象。</param>
        private static void SendResponseBySessionId<T>(global::Network.ISession gameSession, long sessionId, int msgId, T response)
        {
            byte[] payload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(response), sessionId);
            byte[] packet = PacketBuilder.BuildPacket(msgId, payload, out int packetLength);
            try
            {
                gameSession.Send(packet.AsSpan(0, packetLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        /// <summary>
        /// 序列化响应为 UTF-8 JSON，附加目标会话 ID，并构建发送到指定客户端会话的消息包。
        /// </summary>
        /// <remarks>使用 Shared.RouteMetadata 将目标会话 ID 附加到负载，使用 PacketBuilder 构建字节包。发送后在 finally
        /// 块中将临时缓冲区返回到 System.Buffers.ArrayPool<byte>.Shared。</remarks>
        /// <typeparam name="T">响应对象的类型。</typeparam>
        /// <param name="session">目标客户端会话的封装，用于发送构建后的数据包。</param>
        /// <param name="msgId">用于构建数据包的消息标识符。</param>
        /// <param name="response">要序列化为 UTF-8 JSON 并作为负载发送的响应对象。</param>
        private static void SendSimpleResponse<T>(ClientSessionWrapper session, int msgId, T response)
        {
            var payload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(response), session.SessionId);
            var packet = PacketBuilder.BuildPacket(msgId, payload, out int packetLength);
            try
            {
                session.Send(packet.AsSpan(0, packetLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }
    }
}
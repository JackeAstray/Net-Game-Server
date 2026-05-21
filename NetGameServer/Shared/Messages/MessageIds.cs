using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.Messages
{
    /// <summary>
    /// 定义系统中所有消息的唯一标识符。
    /// 每个消息类型都应该在此类中分配一个唯一的整数 ID，以便在网络通信中正确识别和处理消息。
    /// 消息 ID 的范围可以根据不同的模块或功能进行划分，例如：
    /// - 1000-1999: DB 服务器内部服务通信
    /// - 10000-19999: 登录服务器客户端通信（网关转发过来）
    /// - 20000-99999: 游戏服务器客户端通信（网关转发过来）
    /// </summary>
    public static class MessageIds
    {
        // === DB 服务器内部服务通信 (1000-1999) ===
        public const int DbGetMaxUidReq = 1000;
        public const int DbGetMaxUidRes = 1100;
        public const int DbLoginVerifyReq = 1001;
        public const int DbLoginVerifyRes = 1101;
        public const int DbRegisterVerifyReq = 1002;
        public const int DbRegisterVerifyRes = 1102;
        public const int DbAccountQueryReq = 1003;
        public const int DbAccountQueryRes = 1103;
        public const int DbOnlineStatsReq = 1004;
        public const int DbOnlineStatsRes = 1104;
        public const int DbUpdateOnlineStateReq = 1005;
        public const int DbUpdateOnlineStateRes = 1105;

        // DB Friend Messages
        public const int DbAddFriendReq = 1006;
        public const int DbRemoveFriendReq = 1007;
        public const int DbSetFriendRemarkReq = 1008;
        public const int DbGetFriendsReq = 1009;
        public const int DbChangePasswordReq = 1010;
        public const int DbChangePasswordRes = 1110;
        public const int DbResetPasswordByEmailReq = 1011;
        public const int DbResetPasswordByEmailRes = 1111;
        public const int DbAddBlacklistReq = 1012;
        public const int DbRemoveBlacklistReq = 1013;
        public const int DbGetBlacklistReq = 1014;
        public const int DbResolveUserByUniqueIdReq = 1015;
        public const int DbResolveUserByUserIdReq = 1016;

        // === 登录服务器客户端通信 (10000-19999 网关转发过来) ===
        public const int PlayerDisconnectNotif = 10000;

        public const int LoginReq = 10001;
        public const int LoginRes = 10002;

        public const int RegisterReq = 10003;
        public const int RegisterRes = 10004;

        public const int LogoutReq = 10005;
        public const int LogoutRes = 10006;

        public const int ResetPasswordReq = 10007;
        public const int ResetPasswordRes = 10008;

        public const int UpdateNicknameReq = 10009;
        public const int UpdateNicknameRes = 10010;

        public const int KickedOffNotif = 10011;
        public const int FindPasswordWithCodeReq = 10012;
        public const int FindPasswordWithCodeRes = 10013;

        // === 游戏服务器客户端通信 (20000-29999 网关转发过来) ===
        // public const int PlayerMoveReq = 20001;
        // ...

        // === 中心/调度服务器客户端通信 (30000-39999 网关转发过来) ===
        public const int CenterMatchReq = 30001;
        public const int CenterMatchRes = 30002;
        public const int CenterCreateRoomReq = 30003;
        public const int CenterCreateRoomRes = 30004;
        public const int CenterListRoomsReq = 30005;
        public const int CenterListRoomsRes = 30006;
        public const int CenterJoinRoomReq = 30007;
        public const int CenterJoinRoomRes = 30008;
        public const int CenterCloseRoomReq = 30009;
        public const int CenterCloseRoomRes = 30010;
        public const int RoomClosedNotif = 30011;
        public const int CenterUpdateRoomSettingsReq = 30012;
        public const int CenterUpdateRoomSettingsRes = 30013;
        public const int RoomSettingsChangedNotif = 30014;
        public const int CenterStartRoomGameReq = 30015;
        public const int CenterStartRoomGameRes = 30016;
        public const int RoomGameStartedNotif = 30017;
        public const int RoomMemberListReq = 30018;
        public const int RoomMemberListRes = 30019;
        public const int RoomMemberListChangedNotif = 30020;
        public const int RoomReadyReq = 30021;
        public const int RoomReadyRes = 30022;
        public const int RoomReadyChangedNotif = 30023;
        public const int RoomTransferOwnerReq = 30024;
        public const int RoomTransferOwnerRes = 30025;
        public const int RoomOwnerChangedNotif = 30026;
        public const int RoomKickMemberReq = 30027;
        public const int RoomKickMemberRes = 30028;
        public const int RoomKickedNotif = 30029;
        public const int CenterRoomChatReq = 30030;
        public const int CenterRoomChatRes = 30031;
        public const int CenterRoomChatNotif = 30032;

        // === Center 内部节点通信 (90000-90999，非客户端消息) ===
        public const int CenterRegisterNodeReq = 90001;
        public const int CenterRegisterNodeRes = 90002;
        public const int CenterCreateSceneReq = 90003; // Center -> Battle
        public const int CenterCreateSceneRes = 90004; // Battle -> Center
        public const int CenterNodeStatusReq = 90005;
        public const int CenterDestroySceneReq = 90006; // Center -> Battle
        public const int CenterDestroySceneRes = 90007; // Battle -> Center
        public const int CenterRoomPlayerCountSyncReq = 90008; // Battle -> Center
        public const int CenterRoomPlayerCountSyncRes = 90009;
        public const int CenterRoomMemberLeaveSyncReq = 90010; // Battle -> Center
        public const int CenterRoomMemberLeaveSyncRes = 90011;

        // === 战斗/房间服务器客户端通信 (40000-49999 网关转发过来) ===
        public const int BattleJoinReq = 40001;
        public const int BattleJoinRes = 40002;
        public const int BattleFrameSync = 40003;
        public const int BattleLeaveRoomReq = 40004;
        public const int BattleLeaveRoomRes = 40005;

        // 实体相关同步与广播 (40100-40199)
        public const int EntitySyncReq = 40101;                // 客户端上报自身状态
        public const int EntityEnterViewNotif = 40102;         // 广播：实体进入视野
        public const int EntityLeaveViewNotif = 40103;         // 广播：实体离开视野
        public const int EntityStateUpdateNotif = 40104;       // 广播：实体状态更新

        // === 聊天功能通信 (60000-69999 网关转发过来) ===
        public const int ChatMessageReq = 60001;
        public const int ChatMessageRes = 60002;
        public const int ChatMessageNotif = 60003;

        // === 好友功能通信 (50000-59999 网关转发过来) ===
        public const int AddFriendReq = 50001;
        public const int AddFriendRes = 50002;
        public const int RemoveFriendReq = 50003;
        public const int RemoveFriendRes = 50004;
        public const int SetFriendRemarkReq = 50005;
        public const int SetFriendRemarkRes = 50006;
        public const int GetFriendsReq = 50007;
        public const int GetFriendsRes = 50008;
        public const int InviteGameReq = 50009;
        public const int InviteGameRes = 50010;
        public const int InviteGameNotif = 50011;
        public const int AddBlacklistReq = 50012;
        public const int AddBlacklistRes = 50013;
        public const int RemoveBlacklistReq = 50014;
        public const int RemoveBlacklistRes = 50015;
        public const int GetBlacklistReq = 50016;
        public const int GetBlacklistRes = 50017;

        // === DB 好友/聊天等响应消息 (1100-1199) ===
        public const int DbAddFriendRes = 1106;
        public const int DbRemoveFriendRes = 1107;
        public const int DbSetFriendRemarkRes = 1108;
        public const int DbGetFriendsRes = 1109;
        public const int DbAddBlacklistRes = 1112;
        public const int DbRemoveBlacklistRes = 1113;
        public const int DbGetBlacklistRes = 1114;
        public const int DbResolveUserByUniqueIdRes = 1115;
        public const int DbResolveUserByUserIdRes = 1116;
    }
}
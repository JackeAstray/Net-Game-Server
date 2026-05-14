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
        public const int DbLoginVerifyReq = 1001;
        public const int DbRegisterVerifyReq = 1002;
        public const int DbAccountQueryReq = 1003;
        public const int DbOnlineStatsReq = 1004;
        public const int DbUpdateOnlineStateReq = 1005;

        // DB Friend Messages
        public const int DbAddFriendReq = 1006;
        public const int DbRemoveFriendReq = 1007;
        public const int DbSetFriendRemarkReq = 1008;
        public const int DbGetFriendsReq = 1009;

        // === 登录服务器客户端通信 (10000-19999 网关转发过来) ===
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

        // === 游戏服务器客户端通信 (20000-29999 网关转发过来) ===
        // public const int PlayerMoveReq = 20001;
        // ...

        // === 中心/调度服务器客户端通信 (30000-39999 网关转发过来) ===
        public const int CenterMatchReq = 30001;
        public const int CenterMatchRes = 30002;
        public const int CenterCreateRoomReq = 30003;
        public const int CenterCreateRoomRes = 30004;

        public const int CenterRegisterNodeReq = 30005;
        public const int CenterRegisterNodeRes = 30006;
        public const int CenterCreateSceneReq = 30007; // Center -> Battle
        public const int CenterCreateSceneRes = 30008; // Battle -> Center

        // === 战斗/房间服务器客户端通信 (40000-49999 网关转发过来) ===
        public const int BattleJoinReq = 40001;
        public const int BattleJoinRes = 40002;
        public const int BattleFrameSync = 40003;

        // 实体相关同步与广播 (40100-40199)
        public const int EntitySyncReq = 40101;                // 客户端上报自身状态
        public const int EntityEnterViewNotif = 40102;         // 广播：实体进入视野
        public const int EntityLeaveViewNotif = 40103;         // 广播：实体离开视野
        public const int EntityStateUpdateNotif = 40104;       // 广播：实体状态更新

        // 网关发送给后端服务器的玩家掉线通知
        public const int PlayerDisconnectNotif = 10000;

        // === 聊天功能通信 ===
        public const int ChatMessageReq = 30001;
        public const int ChatMessageRes = 30002;
        public const int ChatMessageNotif = 30003;

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
    }
}

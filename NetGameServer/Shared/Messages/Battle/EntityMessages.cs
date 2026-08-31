using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Shared.Messages.Battle
{
    // 用于表示场景中的实体状态
    public class EntityState
    {
        [JsonPropertyName("entityId")]
        public long EntityId { get; set; }

        [JsonPropertyName("nickname")]
        public string Nickname { get; set; } = string.Empty;

        [JsonPropertyName("position")]
        public Vector3 Position { get; set; } = new Vector3();

        [JsonPropertyName("rotation")]
        public Vector3 Rotation { get; set; } = new Vector3();

        [JsonPropertyName("hp")]
        public int Hp { get; set; }

        [JsonPropertyName("maxHp")]
        public int MaxHp { get; set; }

        [JsonPropertyName("score")]
        public int Score { get; set; }

        [JsonPropertyName("equipment")]
        public List<int> Equipment { get; set; } = new List<int>();
    }

    public class Vector3
    {
        [JsonPropertyName("x")]
        public float X { get; set; }
        [JsonPropertyName("y")]
        public float Y { get; set; }
        [JsonPropertyName("z")]
        public float Z { get; set; }

        public Vector3() { }
        public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    // 客户端发给服务器的同步请求（移动、朝向等）
    public class EntitySyncRequest
    {
        [JsonPropertyName("position")]
        public Vector3 Position { get; set; } = new Vector3();

        [JsonPropertyName("rotation")]
        public Vector3 Rotation { get; set; } = new Vector3();
    }

    // 服务器向客户端广播的新增实体可见通知（AOI进入或者新玩家进入房间）
    public class EntityEnterViewNotification
    {
        [JsonPropertyName("entities")]
        public List<EntityState> Entities { get; set; } = new List<EntityState>();
    }

    // 服务器向客户端广播的实体离开可见通知（AOI离开或玩家退出房间）
    public class EntityLeaveViewNotification
    {
        [JsonPropertyName("entityIds")]
        public List<long> EntityIds { get; set; } = new List<long>();
    }

    // 服务器向客户端广播的实体状态改变（移动、血量变化等）
    public class EntityStateUpdateNotification
    {
        // 可能只包含变化部分，这里简化为下发新的完整简要状态
        [JsonPropertyName("state")]
        public EntityState State { get; set; } = new EntityState();
    }
}
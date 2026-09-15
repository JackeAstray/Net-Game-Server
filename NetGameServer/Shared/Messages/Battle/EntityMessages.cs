using System.Text.Json.Serialization;

namespace Shared.Messages.Battle
{
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
        public Vector3? Position { get; set; }

        [JsonPropertyName("rotation")]
        public Vector3 Rotation { get; set; } = new Vector3();
    }
}
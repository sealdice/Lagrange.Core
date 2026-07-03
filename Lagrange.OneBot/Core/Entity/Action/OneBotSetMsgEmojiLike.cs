using System.Text.Json.Serialization;
using Lagrange.OneBot.Core.Operation.Converters;

namespace Lagrange.OneBot.Core.Entity.Action;

[Serializable]
public class OneBotSetMsgEmojiLike
{
    [JsonPropertyName("message_id")] public int MessageId { get; set; }

    [JsonPropertyName("emoji_id")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public required string EmojiId { get; set; }

    [JsonPropertyName("set")] public bool? Set { get; set; }

    [JsonPropertyName("is_add")] public bool? IsAdd { get; set; }
}

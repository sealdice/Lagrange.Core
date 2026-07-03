using System.Text.Json.Serialization;
using Lagrange.OneBot.Core.Operation.Converters;

namespace Lagrange.OneBot.Core.Entity.Action;

[Serializable]
public class OneBotSetMsgEmojiLike
{
    [JsonPropertyName("message_id")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public required string MessageId { get; set; }

    [JsonPropertyName("emoji_id")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public required string EmojiId { get; set; }

    [JsonPropertyName("set")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? Set { get; set; }

    [JsonPropertyName("is_add")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? IsAdd { get; set; }
}

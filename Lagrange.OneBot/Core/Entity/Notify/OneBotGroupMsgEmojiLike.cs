using System.Text.Json.Serialization;

namespace Lagrange.OneBot.Core.Entity.Notify;

[Serializable]
public class OneBotGroupMsgEmojiLike(uint selfId, uint groupId, uint userId, int messageId, OneBotMsgEmojiLike[] likes, bool isAdd)
    : OneBotNotify(selfId, "group_msg_emoji_like")
{
    [JsonPropertyName("group_id")] public uint GroupId { get; } = groupId;

    [JsonPropertyName("user_id")] public uint UserId { get; } = userId;

    [JsonPropertyName("message_id")] public int MessageId { get; } = messageId;

    [JsonPropertyName("likes")] public OneBotMsgEmojiLike[] Likes { get; } = likes;

    [JsonPropertyName("is_add")] public bool IsAdd { get; } = isAdd;
}

[Serializable]
public class OneBotMsgEmojiLike(string emojiId, uint count)
{
    [JsonPropertyName("emoji_id")] public string EmojiId { get; } = emojiId;

    [JsonPropertyName("count")] public uint Count { get; } = count;
}

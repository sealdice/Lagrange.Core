using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.OneBot.Core.Entity.Action;
using Lagrange.OneBot.Core.Operation.Converters;
using Lagrange.OneBot.Database;
using Lagrange.OneBot.Utility;
using static Lagrange.Core.Message.MessageChain;

namespace Lagrange.OneBot.Core.Operation.Generic;

[Operation("set_msg_emoji_like")]
public class SetMsgEmojiLikeOperation(MessageRecordStore messageRecords) : IOperation
{
    public async Task<OneBotResult> HandleOperation(BotContext context, JsonNode? payload)
    {
        if (payload.Deserialize<OneBotSetMsgEmojiLike>(SerializerOptions.DefaultOptions) is not { } data)
        {
            throw new Exception();
        }

        if (!uint.TryParse(data.EmojiId, out uint emojiId))
        {
            return new OneBotResult(null, 1, "invalid emoji_id");
        }

        if (!int.TryParse(data.MessageId, out int messageId))
        {
            return new OneBotResult(null, 1, "invalid message_id");
        }

        bool isAdd = ParseOptionalBoolean(data.Set) ?? ParseOptionalBoolean(data.IsAdd) ?? true;
        var target = messageRecords.GetEmojiLikeTarget(messageId);
        if (target == null) return new OneBotResult(null, 1404, "message not found");

        bool result = target.Type switch
        {
            MessageType.Group => await context.GroupSetMessageReaction((uint)target.ToUin, (uint)target.Sequence, emojiId.ToString(), isAdd),
            MessageType.Friend or MessageType.Temp when isAdd => await context.FriendJoinEmojiChain(GetPeerUin(context, target), emojiId, (uint)target.Sequence),
            MessageType.Friend or MessageType.Temp => false,
            _ => false
        };

        return new OneBotResult(null, result ? 0 : 1, result ? "ok" : "failed");
    }

    private static bool? ParseOptionalBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (bool.TryParse(value, out bool result)) return result;
        if (int.TryParse(value, out int number)) return number != 0;
        return null;
    }

    private static uint GetPeerUin(BotContext context, MessageRecordStore.EmojiLikeTarget target)
    {
        ulong botUin = context.BotUin;
        return (uint)(target.FromUin == botUin ? target.ToUin : target.FromUin);
    }
}

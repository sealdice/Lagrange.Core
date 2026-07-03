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
public class SetMsgEmojiLikeOperation(RealmHelper? realm = null) : IOperation
{
#if !ONEBOT_DISABLE_REALM
    private readonly RealmHelper? _realm = realm;
#endif

    public async Task<OneBotResult> HandleOperation(BotContext context, JsonNode? payload)
    {
#if ONEBOT_DISABLE_REALM
        return new OneBotResult(null, 1404, "realm disabled");
#else
        if (_realm is null) return new OneBotResult(null, 1404, "realm disabled");
        if (payload.Deserialize<OneBotSetMsgEmojiLike>(SerializerOptions.DefaultOptions) is not { } data)
        {
            throw new Exception();
        }

        if (!uint.TryParse(data.EmojiId, out uint emojiId))
        {
            return new OneBotResult(null, 1, "invalid emoji_id");
        }

        bool isAdd = data.Set ?? data.IsAdd ?? true;
        EmojiLikeTarget? target = _realm.Do(realm =>
        {
            MessageRecord? record = realm.All<MessageRecord>().FirstOrDefault(record => record.Id == data.MessageId);
            return record == null
                ? null
                : new EmojiLikeTarget(record.Type, record.Sequence, record.FromUin, record.ToUin);
        });
        if (target == null) return new OneBotResult(null, 1404, "message not found");

        bool result = target.Type switch
        {
            MessageType.Group => await context.GroupSetMessageReaction((uint)target.ToUin, (uint)target.Sequence, emojiId.ToString(), isAdd),
            MessageType.Friend or MessageType.Temp when isAdd => await context.FriendJoinEmojiChain(GetPeerUin(context, target), emojiId, (uint)target.Sequence),
            MessageType.Friend or MessageType.Temp => false,
            _ => false
        };

        return new OneBotResult(null, result ? 0 : 1, result ? "ok" : "failed");
#endif
    }

#if !ONEBOT_DISABLE_REALM
    private sealed record EmojiLikeTarget(MessageType Type, ulong Sequence, ulong FromUin, ulong ToUin);

    private static uint GetPeerUin(BotContext context, EmojiLikeTarget target)
    {
        ulong botUin = context.BotUin;
        return (uint)(target.FromUin == botUin ? target.ToUin : target.FromUin);
    }
#endif
}

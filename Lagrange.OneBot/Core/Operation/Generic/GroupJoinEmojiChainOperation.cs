using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.OneBot.Core.Entity.Action;
using Lagrange.OneBot.Core.Operation.Converters;
using Lagrange.OneBot.Database;
using Lagrange.OneBot.Utility;

namespace Lagrange.OneBot.Core.Operation.Generic;


[Operation(".join_group_emoji_chain")]
public class GroupJoinEmojiChainOperation(MessageRecordStore messageRecords) : IOperation
{
    public async Task<OneBotResult> HandleOperation(BotContext context, JsonNode? payload)
    {
        if (payload.Deserialize<OneBotGroupJoinEmojiChain>(SerializerOptions.DefaultOptions) is { } data)
        {
            var sequence = messageRecords.GetSequence(data.MessageId);
            if (sequence == null) return new OneBotResult(null, 1404, "message not found");

            bool res = await context.GroupJoinEmojiChain(data.GroupId, data.EmojiId, (uint)sequence);
            return new OneBotResult(null, res ? 0 : -1, res ? "ok" : "failed");
        }
        throw new Exception();
    }
}

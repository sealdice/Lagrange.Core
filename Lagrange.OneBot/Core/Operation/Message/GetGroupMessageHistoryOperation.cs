using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.OneBot.Core.Entity.Action;
using Lagrange.OneBot.Core.Entity.Message;
using Lagrange.OneBot.Core.Operation.Converters;
using Lagrange.OneBot.Database;
using Lagrange.OneBot.Message;
using Lagrange.OneBot.Utility;

namespace Lagrange.OneBot.Core.Operation.Message;

[Operation("get_group_msg_history")]
public class GetGroupMessageHistoryOperation(MessageService message, MessageRecordStore messageRecords) : IOperation
{
    private readonly MessageService _message = message;

    public async Task<OneBotResult> HandleOperation(BotContext context, JsonNode? payload)
    {
        if (payload.Deserialize<OneBotGroupMsgHistory>(SerializerOptions.DefaultOptions) is { } history)
        {
            var sequence = messageRecords.GetGroupSequenceForHistory(history.GroupId, history.MessageId);
            if (sequence == null) return new OneBotResult(null, 1404, "message not found");

            uint start = sequence.Value > history.Count ? sequence.Value - (uint)history.Count + 1 : 0;
            if (await context.GetGroupMessage(history.GroupId, start, sequence.Value) is { } results)
            {
                var messages = results
                    .Select(x => _message.ConvertToGroupMsg(context.BotUin, x))
                    .ToList();
                return new OneBotResult(new OneBotGroupMsgHistoryResponse(messages), 0, "ok");
            }
        }

        throw new Exception();
    }
}

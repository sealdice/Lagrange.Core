using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.OneBot.Core.Entity.Action;
using Lagrange.OneBot.Core.Entity.Message;
using Lagrange.OneBot.Core.Operation.Converters;
using Lagrange.OneBot.Database;
using Lagrange.OneBot.Message;
using Lagrange.OneBot.Utility;

namespace Lagrange.OneBot.Core.Operation.Message;

[Operation("get_friend_msg_history")]
public class GetFriendMessageHistoryOperation(MessageService message, MessageRecordStore messageRecords) : IOperation
{
    private readonly MessageService _message = message;

    public async Task<OneBotResult> HandleOperation(BotContext context, JsonNode? payload)
    {
        if (payload.Deserialize<OneBotFriendMsgHistory>(SerializerOptions.DefaultOptions) is { } history)
        {
            var chain = messageRecords.GetFriendHistoryChain(history.UserId, history.MessageId);
            if (chain == null) return new OneBotResult(null, 1404, "message not found");

            if (await context.GetRoamMessage(chain, history.Count) is { } results)
            {
                var messages = results
                    .Select(x => _message.ConvertToPrivateMsg(context.BotUin, x))
                    .ToList();
                return new OneBotResult(new OneBotFriendMsgHistoryResponse(messages), 0, "ok");
            }
        }

        throw new Exception();
    }
}

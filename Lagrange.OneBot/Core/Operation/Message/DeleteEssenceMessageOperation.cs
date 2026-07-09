using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.OneBot.Core.Entity.Action;
using Lagrange.OneBot.Core.Operation.Converters;
using Lagrange.OneBot.Database;
using Lagrange.OneBot.Utility;

namespace Lagrange.OneBot.Core.Operation.Message;

[Operation("delete_essence_msg")]
public class DeleteEssenceMessageOperation(MessageRecordStore messageRecords) : IOperation
{
    public async Task<OneBotResult> HandleOperation(BotContext context, JsonNode? payload)
    {
        if (payload.Deserialize<OneBotGetMessage>(SerializerOptions.DefaultOptions) is { } getMsg)
        {
            var chain = messageRecords.GetMessageChain(getMsg.MessageId);
            if (chain == null) return new OneBotResult(null, 1404, "message not found");

            bool result = await context.RemoveEssenceMessage(chain);
            return new OneBotResult(null, result ? 0 : 1, result ? "ok" : "failed");
        }

        throw new Exception();
    }
}

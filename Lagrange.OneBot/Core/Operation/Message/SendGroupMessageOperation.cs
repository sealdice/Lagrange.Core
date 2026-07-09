using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.OneBot.Core.Entity.Action;
using Lagrange.OneBot.Core.Entity.Action.Response;
using Lagrange.OneBot.Core.Operation.Converters;
using Lagrange.OneBot.Database;
using Lagrange.OneBot.Utility;
using MessagePack;
using static Lagrange.Core.Message.MessageChain;

namespace Lagrange.OneBot.Core.Operation.Message;

[Operation("send_group_msg")]
public sealed class SendGroupMessageOperation(MessageCommon common, MessageRecordStore messageRecords) : IOperation
{
    public async Task<OneBotResult> HandleOperation(BotContext context, JsonNode? payload)
    {
        var chain = payload.Deserialize<OneBotGroupMessageBase>(SerializerOptions.DefaultOptions) switch
        {
            OneBotGroupMessage message => common.ParseChain(message).Build(),
            OneBotGroupMessageSimple messageSimple => common.ParseChain(messageSimple).Build(),
            OneBotGroupMessageText messageText => common.ParseChain(messageText).Build(),
            _ => throw new Exception()
        };

        var result = await context.SendMessage(chain);

        if (result.Result != 0) return new OneBotResult(null, (int)result.Result, "failed");
        if (result.Sequence == null || result.Sequence == 0) return new OneBotResult(null, 9000, "failed");

        int hash = MessageRecord.CalcMessageHash(chain.MessageId, result.Sequence ?? 0);

        messageRecords.Save(new MessageRecord
        {
            Id = hash,
            Type = MessageType.Group,
            Sequence = result.Sequence ?? 0,
            ClientSequence = result.ClientSequence,
            MessageId = chain.MessageId,
            Time = DateTimeOffset.FromUnixTimeSeconds(result.Timestamp),
            FromUin = context.BotUin,
            ToUin = chain.GroupUin ?? 0,
            Entities = MessagePackSerializer.Serialize<List<IMessageEntity>>(chain, MessageRecord.OPTIONS)
        });

        return new OneBotResult(new OneBotMessageResponse(hash), (int)result.Result, "ok");
    }
}

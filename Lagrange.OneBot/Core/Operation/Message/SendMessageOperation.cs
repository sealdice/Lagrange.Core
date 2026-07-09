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

[Operation("send_msg")]
public sealed class SendMessageOperation(MessageCommon common, MessageRecordStore messageRecords) : IOperation
{
    public async Task<OneBotResult> HandleOperation(BotContext context, JsonNode? payload)
    {
        var chain = payload.Deserialize<OneBotMessageBase>(SerializerOptions.DefaultOptions) switch
        {
            OneBotMessage message => common.ParseChain(message).Build(),
            OneBotMessageSimple messageSimple => common.ParseChain(messageSimple).Build(),
            OneBotMessageText messageText => common.ParseChain(messageText).Build(),
            _ => throw new Exception()
        };

        var result = await context.SendMessage(chain);

        if (result.Result != 0) return new OneBotResult(null, (int)result.Result, "failed");
        if (result.Sequence == null || result.Sequence == 0) return new OneBotResult(null, 9000, "failed");

        ulong messageId = chain.IsGroup ? chain.MessageId : result.MessageId;
        int obid = MessageRecord.CalcMessageHash(messageId, result.Sequence ?? 0);

        messageRecords.Save(new MessageRecord
        {
            Id = obid,
            Type = chain.IsGroup ? MessageType.Group : MessageType.Friend,
            Sequence = result.Sequence ?? 0,
            ClientSequence = result.ClientSequence,
            MessageId = messageId,
            Time = DateTimeOffset.FromUnixTimeSeconds(result.Timestamp),
            FromUin = context.BotUin,
            ToUin = chain.IsGroup ? chain.GroupUin ?? 0 : chain.FriendUin,
            Entities = MessagePackSerializer.Serialize<List<IMessageEntity>>(chain, MessageRecord.OPTIONS)
        });

        return new OneBotResult(new OneBotMessageResponse(obid), 0, "ok");
    }
}

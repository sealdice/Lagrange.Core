using System.Text.Json.Serialization;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entity;
using Lagrange.OneBot.Database;
using Lagrange.OneBot.Core.Operation.Converters;

namespace Lagrange.OneBot.Message.Entity;

[Serializable]
public partial class ReplySegment(uint messageId)
{
    public ReplySegment() : this(0) { }

    [JsonPropertyName("id")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    [CQProperty]
    public string MessageId { get; set; } = messageId.ToString();
}

[SegmentSubscriber(typeof(ForwardEntity), "reply")]
public partial class ReplySegment : SegmentBase
{
    internal MessageChain? TargetChain { get; set; }

    internal uint Sequence { get; private set; }

    public override void Build(MessageBuilder builder, SegmentBase segment)
    {
        if (segment is ReplySegment reply && MessageRecords is not null)
        {
            if (!int.TryParse(reply.MessageId, out int messageId)) return;

            var chain = MessageRecords.GetMessageChain(messageId);
            if (chain == null) return;

            reply.TargetChain ??= chain;

            var build = MessagePacker.Build(reply.TargetChain, "");
            var virtualElem = build.Body?.RichText?.Elems;
            if (virtualElem != null) reply.TargetChain.Elements.AddRange(virtualElem);

            builder.Forward(reply.TargetChain);
        }
    }

    public override SegmentBase? FromEntity(MessageChain chain, IMessageEntity entity)
    {
        if (entity is not ForwardEntity forward) throw new ArgumentException("The entity is not a forward entity.");
        if (MessageRecords is null) return null;

        int? id;
        if (chain.IsGroup)
        {
            id = MessageRecord.CalcMessageHash(forward.MessageId, forward.Sequence);
        }
        else
        {
            ulong? sequence = MessageRecords.FindFriendSequence(
                chain.FriendUin,
                forward.ClientSequence,
                0x01000000UL << 32 | forward.MessageId);
            id = sequence == null ? null : MessageRecord.CalcMessageHash(forward.MessageId, (uint)sequence);
        }

        return new ReplySegment { MessageId = (id ?? 0).ToString() };
    }
}

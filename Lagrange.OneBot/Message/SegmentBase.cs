using Lagrange.Core.Message;
using Lagrange.OneBot.Utility;

namespace Lagrange.OneBot.Message;

public abstract class SegmentBase
{
    public MessageRecordStore? MessageRecords { protected get; set; }
    
    public abstract void Build(MessageBuilder builder, SegmentBase segment);
    
    public abstract SegmentBase? FromEntity(MessageChain chain, IMessageEntity entity);
}

using Lagrange.Core.Common.Entity;
#if !ONEBOT_DISABLE_REALM
using Realms;
#endif

namespace Lagrange.OneBot.Database;

public partial class MessageStyleRecord
#if !ONEBOT_DISABLE_REALM
    : IRealmObject
#endif
{
#if !ONEBOT_DISABLE_REALM
    [MapTo(nameof(BubbleId))]
#endif
    public long BubbleIdLong { get; set; }
    public ulong BubbleId { get => (ulong)BubbleIdLong; set => BubbleIdLong = (long)value; }

#if !ONEBOT_DISABLE_REALM
    [MapTo(nameof(PendantId))]
#endif
    public long PendantIdLong { get; set; }
    public ulong PendantId { get => (ulong)PendantIdLong; set => PendantIdLong = (long)value; }

#if !ONEBOT_DISABLE_REALM
    [MapTo(nameof(FontIdShort))]
#endif
    public short FontIdShort { get; set; }
    public ushort FontId { get => (ushort)FontIdShort; set => FontIdShort = (short)value; }

#if !ONEBOT_DISABLE_REALM
    [MapTo(nameof(FontEffectId))]
#endif
    public int FontEffectIdInt { get; set; }
    public uint FontEffectId { get => (uint)FontEffectIdInt; set => FontEffectIdInt = (int)value; }

    public bool IsCsFontEffectEnabled { get; set; }

#if !ONEBOT_DISABLE_REALM
    [MapTo(nameof(BubbleDiyTextId))]
#endif
    public int BubbleDiyTextIdInt { get; set; }
    public uint BubbleDiyTextId { get => (uint)BubbleDiyTextIdInt; set => BubbleDiyTextIdInt = (int)value; }


    public static implicit operator MessageStyleRecord(MessageStyle style) => new()
    {
        BubbleId = style.BubbleId,
        PendantId = style.PendantId,
        FontId = style.FontId,
        FontEffectId = style.FontEffectId,
        IsCsFontEffectEnabled = style.IsCsFontEffectEnabled,
        BubbleDiyTextId = style.BubbleDiyTextId
    };

    public static implicit operator MessageStyle(MessageStyleRecord record) => new()
    {
        BubbleId = record.BubbleId,
        PendantId = record.PendantId,
        FontId = record.FontId,
        FontEffectId = record.FontEffectId,
        IsCsFontEffectEnabled = record.IsCsFontEffectEnabled,
        BubbleDiyTextId = record.BubbleDiyTextId
    };
}

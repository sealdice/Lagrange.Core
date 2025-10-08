using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.OneBot.Core.Entity.Action;
using Lagrange.OneBot.Core.Operation.Converters;
using Lagrange.OneBot.Database;
using Lagrange.OneBot.Utility;

namespace Lagrange.OneBot.Core.Operation.Group;

[Operation("set_group_reaction")]
public class SetGroupReactionOperation(RealmHelper? realm = null) : IOperation
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
        if (payload.Deserialize<OneBotSetGroupReaction>(SerializerOptions.DefaultOptions) is { } data)
        {
            var sequence = _realm.Do(realm => realm.All<MessageRecord>()
                .First(record => record.Id == data.MessageId)
                .Sequence);

            bool result = await context.GroupSetMessageReaction(
                data.GroupId,
                (uint)sequence,
                data.Code,
                data.IsAdd
            );
            return new OneBotResult(null, result ? 0 : 1, result ? "ok" : "failed");
        }

        throw new Exception();
#endif
    }
}

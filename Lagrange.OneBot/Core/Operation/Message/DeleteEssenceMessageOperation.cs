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
public class DeleteEssenceMessageOperation(RealmHelper? realm = null) : IOperation
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
        if (payload.Deserialize<OneBotGetMessage>(SerializerOptions.DefaultOptions) is { } getMsg)
        {
            var chain = _realm.Do<MessageChain>(realm => realm.All<MessageRecord>()
                .First(record => record.Id == getMsg.MessageId));

            bool result = await context.RemoveEssenceMessage(chain);
            return new OneBotResult(null, result ? 0 : 1, result ? "ok" : "failed");
        }

        throw new Exception();
#endif
    }
}

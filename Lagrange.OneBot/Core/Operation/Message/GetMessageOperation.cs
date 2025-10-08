using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.Core.Message;
using Lagrange.OneBot.Core.Entity.Action;
using Lagrange.OneBot.Core.Entity.Action.Response;
using Lagrange.OneBot.Core.Entity.Message;
using Lagrange.OneBot.Core.Operation.Converters;
using Lagrange.OneBot.Database;
using Lagrange.OneBot.Message;
using Lagrange.OneBot.Utility;

namespace Lagrange.OneBot.Core.Operation.Message;

[Operation("get_msg")]
public class GetMessageOperation(MessageService service, RealmHelper? realm = null) : IOperation
{
#if !ONEBOT_DISABLE_REALM
    private readonly RealmHelper _realm = realm ?? throw new ArgumentNullException(nameof(realm));
#endif
    private readonly MessageService _service = service;

    public async Task<OneBotResult> HandleOperation(BotContext context, JsonNode? payload)
    {
#if ONEBOT_DISABLE_REALM
        return new OneBotResult(null, 1404, "realm disabled");
#else
        if (payload.Deserialize<OneBotGetMessage>(SerializerOptions.DefaultOptions) is { } getMsg)
        {
            var chain = _realm.Do<MessageChain>(realm => realm.All<MessageRecord>().First(record => record.Id == getMsg.MessageId));

            OneBotSender sender = chain.Type switch
            {
                MessageChain.MessageType.Group => new(
                    chain.FriendUin,
                    (await context.FetchMembers((uint)chain.GroupUin!))
                        .First(member => member.Uin == chain.FriendUin)
                        .MemberName
                ),
                MessageChain.MessageType.Temp => new(chain.FriendUin, string.Empty),
                MessageChain.MessageType.Friend => new(
                    chain.FriendUin,
                    (await context.FetchFriends())
                        .First(friend => friend.Uin == chain.FriendUin)
                        .Nickname
                ),
                _ => throw new NotImplementedException(),
            };

            var elements = _service.Convert(chain);
            var response = new OneBotGetMessageResponse(
                chain.Time,
                chain.Type == MessageChain.MessageType.Group ? "group" : "private",
                MessageRecord.CalcMessageHash(chain.MessageId, chain.Sequence),
                sender,
                elements
            );

            return new OneBotResult(response, 0, "ok");
        }

        throw new Exception();
#endif
    }
}

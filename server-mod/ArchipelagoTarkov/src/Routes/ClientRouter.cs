using ArchipelagoTarkov.Session;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace ArchipelagoTarkov.Routes;

/// <summary>
/// Endpoints used by the optional BepInEx client plugin.
/// GET /archipelago/client/poll         -> {"messages":[...], "kill":bool, "cause":string}
/// GET /archipelago/client/killed       -> acknowledges that a DeathLink kill was carried out
/// </summary>
[Injectable]
public class ClientRouter(JsonUtil jsonUtil, HttpResponseUtil httpResponseUtil)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/archipelago/client/poll",
                (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(httpResponseUtil.NoBody(Poll(sessionId.ToString())))
            ),
            new RouteAction<EmptyRequestData>(
                "/archipelago/client/killed",
                (url, info, sessionId, output, cancellationToken) =>
                {
                    var session = SessionManager.Instance?.Get(sessionId.ToString());
                    if (session != null)
                    {
                        lock (session)
                        {
                            session.AcknowledgeDeathLinkKill();
                        }
                    }

                    return new ValueTask<string>(httpResponseUtil.NoBody(new Dictionary<string, object> { ["ok"] = true }));
                }
            ),
        ]
    )
{
    private static Dictionary<string, object> Poll(string profileId)
    {
        var session = SessionManager.Instance?.Get(profileId);
        if (session is null)
        {
            return new Dictionary<string, object> { ["linked"] = false, ["messages"] = new List<string>(), ["kill"] = false, ["cause"] = "" };
        }

        lock (session)
        {
            return new Dictionary<string, object>
            {
                ["linked"] = session.Active,
                ["connected"] = session.ConnectionState == Ap.ApConnectionState.Connected,
                ["messages"] = session.TakeClientMessages(),
                ["kill"] = session.PendingDeathLinkKill,
                ["cause"] = session.PendingDeathLinkCause ?? "",
            };
        }
    }
}

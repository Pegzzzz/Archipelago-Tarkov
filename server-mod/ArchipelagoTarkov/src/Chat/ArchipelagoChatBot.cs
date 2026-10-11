using System.Text.RegularExpressions;
using ArchipelagoTarkov.Ap;
using ArchipelagoTarkov.Game;
using ArchipelagoTarkov.Session;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Dialogue;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Dialog;
using SPTarkov.Server.Core.Models.Eft.Profile;

namespace ArchipelagoTarkov.Chat;

/// <summary>The "Archipelago" contact in the in-game messenger. All player commands go through it.</summary>
[Injectable(InjectionType.Singleton)]
public class ArchipelagoChatBot(GameBridge game) : IDialogueChatBot
{
    private static readonly Regex ConnectPattern = new(
        @"^connect\s+(?<server>\S+)\s+(?:""(?<slotq>[^""]+)""|(?<slot>\S+))(?:\s+(?<password>.+))?$",
        RegexOptions.IgnoreCase);

    public const string HelpText =
        "Archipelago commands:\n" +
        " connect <server:port> <slot> [password] - link this profile to a slot (use quotes for names with spaces)\n" +
        " status - connection, unlocked maps/traders and goal progress\n" +
        " items - everything you have received\n" +
        " hint <item> - ask the server for a hint (same as !hint)\n" +
        " !<command> - any Archipelago server command, e.g. !hint Customs Access\n" +
        " disconnect - stop connecting (progress is kept)\n" +
        " reconnect - connect again with the saved settings\n" +
        " reset - forget this profile's Archipelago game and unlock everything again\n" +
        "Any other text is sent to the multiworld chat.";

    public UserDialogInfo GetChatBot() => GameBridge.ChatBotInfo;

    public ValueTask<string> HandleMessage(MongoId sessionId, SendMessageRequest request)
    {
        var manager = SessionManager.Instance;
        var profileId = sessionId.ToString();
        var text = (request.Text ?? "").Trim();
        if (manager is null)
        {
            game.Say(profileId, "The Archipelago mod failed to load; check the SPT server console.");
            return new ValueTask<string>(request.DialogId);
        }

        try
        {
            Handle(manager, profileId, text);
        }
        catch (Exception ex)
        {
            game.Say(profileId, $"Something went wrong: {ex.Message}");
        }

        return new ValueTask<string>(request.DialogId);
    }

    private void Handle(SessionManager manager, string profileId, string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower is "" or "help" or "?")
        {
            game.Say(profileId, HelpText);
            return;
        }

        var connect = ConnectPattern.Match(text);
        if (connect.Success)
        {
            var server = connect.Groups["server"].Value;
            var slot = connect.Groups["slotq"].Success ? connect.Groups["slotq"].Value : connect.Groups["slot"].Value;
            var password = connect.Groups["password"].Success ? connect.Groups["password"].Value.Trim() : null;
            if (!ApClient.IsValidAddress(server, out var addressError))
            {
                game.Say(profileId, addressError ?? "That server address is not valid.");
                return;
            }

            var session = manager.GetOrCreate(profileId);
            lock (session)
            {
                var error = session.Configure(server, slot, password);
                session.Say(error ?? $"Connecting to {server} as {slot}...");
            }

            return;
        }

        if (lower.StartsWith("connect"))
        {
            game.Say(profileId, "Usage: connect <server:port> <slot name> [password]\nExample: connect archipelago.gg:38281 MySlot");
            return;
        }

        var existing = manager.Get(profileId);
        if (existing is null)
        {
            game.Say(profileId, "This profile is not linked to Archipelago yet.\n" + HelpText);
            return;
        }

        lock (existing)
        {
            switch (lower)
            {
                case "status":
                    existing.Say(existing.StatusText());
                    return;
                case "items":
                    existing.Say(existing.ItemsText());
                    return;
                case "disconnect":
                    existing.Disconnect();
                    existing.Say("Disconnected. Send 'reconnect' to connect again.");
                    return;
                case "reconnect":
                    if (!existing.State.IsConfigured)
                    {
                        existing.Say("No saved connection. Use: connect <server:port> <slot> [password]");
                        return;
                    }

                {
                    var error = existing.Configure(existing.State.Server!, existing.State.Slot!, existing.State.Password);
                    existing.Say(error ?? $"Connecting to {existing.State.Server} as {existing.State.Slot}...");
                    return;
                }
                case "reset":
                    existing.Say("This wipes this profile's Archipelago progress and unlocks all maps and traders. " +
                                 "Send 'reset confirm' to do it.");
                    return;
                case "reset confirm":
                    existing.ResetGame();
                    existing.Say("Archipelago progress for this profile was reset. Send 'connect ...' to link a new game.");
                    return;
            }

            if (lower.StartsWith("hint "))
            {
                existing.SendChat("!hint " + text[5..].Trim());
                return;
            }

            existing.SendChat(text);
        }
    }
}

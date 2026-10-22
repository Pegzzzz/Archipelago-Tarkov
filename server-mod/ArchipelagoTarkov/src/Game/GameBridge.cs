using ArchipelagoTarkov.Data;
using ArchipelagoTarkov.State;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Dialog;
using SPTarkov.Server.Core.Servers;

namespace ArchipelagoTarkov.Game;

/// <summary>Everything that reads or changes an SPT profile goes through here.</summary>
[Injectable(InjectionType.Singleton)]
public class GameBridge(
    ISptLogger<GameBridge> logger,
    ProfileHelper profileHelper,
    SaveServer saveServer,
    MailSendService mailSendService,
    TraderHelper traderHelper,
    LootPools lootPools,
    TraderConfig traderConfig
)
{
    /// <summary>Flea bans the mod adds carry this exact timestamp (1 Jan 2100) so they can be told apart from real bans.</summary>
    public const long FleaBanTimestamp = 4_102_444_800;

    /// <summary>Message storage time for delivered items: one year (long asyncs).</summary>
    public const long MailStorageSeconds = 365L * 24 * 3600;

    /// <summary>Older Archipelago chat lines beyond this are trimmed so the profile does not grow forever.</summary>
    public const int MaxBotMessages = 400;

    public static readonly MongoId ChatBotId = new("6727ac0f4a7b9e3d2c1f0a01");

    public static UserDialogInfo ChatBotInfo { get; } = new()
    {
        Id = ChatBotId,
        Aid = 1234500,
        Info = new UserDialogDetails
        {
            Nickname = "Archipelago",
            Side = "Usec",
            Level = 1,
            MemberCategory = MemberCategory.Default,
            SelectedMemberCategory = MemberCategory.Default,
        },
    };

    public bool ProfileExists(string profileId)
    {
        try
        {
            return saveServer.ProfileExists(new MongoId(profileId)) && profileHelper.GetPmcProfile(new MongoId(profileId))?.Info != null;
        }
        catch
        {
            return false;
        }
    }

    public PmcData? Pmc(string profileId)
    {
        try
        {
            return profileHelper.GetPmcProfile(new MongoId(profileId));
        }
        catch
        {
            return null;
        }
    }

    public string ProfileName(string profileId) => Pmc(profileId)?.Info?.Nickname ?? profileId;

    // ------------------------------------------------------------------ messages
    public void Say(string profileId, string text)
    {
        try
        {
            mailSendService.SendUserMessageToPlayer(new MongoId(profileId), ChatBotInfo, text, [], null);
        }
        catch (Exception ex)
        {
            logger.Warning($"[Archipelago] could not message {profileId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Send items to the player. The game only lets you take attachments from system messages (that's also how
    /// SPT's own "give" command delivers), so items always go out as a system message; never from a chat contact.
    /// </summary>
    public void SendItems(string profileId, string text, List<Item> items)
    {
        if (items.Count == 0)
        {
            Say(profileId, text);
            return;
        }

        mailSendService.SendSystemMessageToPlayer(new MongoId(profileId), "Archipelago: " + text, items, MailStorageSeconds);
    }

    /// <summary>
    /// Versions up to 1.1.0 attached items to messages from the Archipelago contact, which the game can't collect.
    /// Send whatever is still attached to those messages again as system messages, and take it off the old ones.
    /// Returns how many messages were moved.
    /// </summary>
    public int ResendStuckAttachments(string profileId)
    {
        var full = profileHelper.GetFullProfile(new MongoId(profileId));
        if (full?.DialogueRecords is null || !full.DialogueRecords.TryGetValue(ChatBotId, out var dialog) || dialog.Messages is null)
        {
            return 0;
        }

        var stuck = dialog.Messages
            .Where(m => m.HasRewards == true && m.RewardCollected != true && (m.Items?.Data?.Count ?? 0) > 0)
            .OrderBy(m => m.DateTime ?? 0)
            .ToList();
        foreach (var message in stuck)
        {
            var items = message.Items!.Data!.ToList();
            mailSendService.SendSystemMessageToPlayer(new MongoId(profileId), "Archipelago (sent again): " + (message.Text ?? ""), items,
                MailStorageSeconds);
            message.Items = null;
            message.HasRewards = false;
            message.RewardCollected = false;
            message.MaxStorageTime = null;
            message.Text = (message.Text ?? "") + "\n(Moved to your System messages.)";
        }

        if (stuck.Count > 0)
        {
            dialog.AttachmentsNew = 0;
        }

        return stuck.Count;
    }

    /// <summary>
    /// Send a system message whose attachment, once collected, unlocks the trader in the running client
    /// immediately (the same mechanism BSG uses). The server profile is unlocked separately.
    /// </summary>
    public void SendTraderUnlockMail(string profileId, string traderId, string traderName)
    {
        var token = new Item
        {
            Id = new MongoId(),
            Template = Money.ROUBLES,
            Upd = new Upd { StackObjectsCount = 1 },
        };
        mailSendService.SendSystemMessageToPlayer(
            new MongoId(profileId),
            $"Archipelago: {traderName} is now available. Receive this message to see them right away.",
            [token],
            MailStorageSeconds,
            [new ProfileChangeEvent { Id = new MongoId(), Type = "UnlockTrader", Value = 1, Entity = traderId }]
        );
    }

    // ------------------------------------------------------------------ locks
    /// <summary>Make the profile's trader and flea state match the items received so far. Returns true if anything changed.</summary>
    public bool EnforceLocks(string profileId, TarkovData data, ISet<string> unlockedTraders, bool fleaLocked)
    {
        var pmc = Pmc(profileId);
        if (pmc?.TradersInfo is null || pmc.Info is null)
        {
            return false;
        }

        var changed = false;
        foreach (var trader in data.Traders)
        {
            if (!pmc.TradersInfo.TryGetValue(new MongoId(trader.Id), out var info))
            {
                continue;
            }

            var shouldBeUnlocked = unlockedTraders.Contains(trader.Key);
            if ((info.Unlocked ?? false) != shouldBeUnlocked)
            {
                info.Unlocked = shouldBeUnlocked;
                changed = true;
            }
        }

        var bans = pmc.Info.Bans?.ToList() ?? [];
        var ourBan = bans.FirstOrDefault(b => b.BanType == BanType.RagFair && b.DateTime == FleaBanTimestamp);
        if (fleaLocked && ourBan is null)
        {
            bans.Add(new Ban { BanType = BanType.RagFair, DateTime = FleaBanTimestamp });
            pmc.Info.Bans = bans;
            changed = true;
        }
        else if (!fleaLocked && ourBan is not null)
        {
            bans.Remove(ourBan);
            pmc.Info.Bans = bans;
            changed = true;
        }

        return changed;
    }

    /// <summary>Remove everything the mod changed on the profile (used by the reset command).</summary>
    public void RemoveLocks(string profileId, TarkovData data)
    {
        var pmc = Pmc(profileId);
        if (pmc?.TradersInfo is null || pmc.Info is null)
        {
            return;
        }

        foreach (var trader in data.Traders)
        {
            if (!pmc.TradersInfo.TryGetValue(new MongoId(trader.Id), out var info))
            {
                continue;
            }

            // Jaeger is normally unlocked by Mechanic's "Introduction" quest.
            info.Unlocked = trader.Key != "jaeger" || (data.JaegerUnlockQuestId != null && QuestCompleted(profileId, data.JaegerUnlockQuestId));
        }

        pmc.Info.Bans = (pmc.Info.Bans ?? []).Where(b => !(b.BanType == BanType.RagFair && b.DateTime == FleaBanTimestamp)).ToList();
    }

    // ------------------------------------------------------------------ checks
    public IEnumerable<long> DetectChecks(string profileId, TarkovData data, ProfileState state)
    {
        var pmc = Pmc(profileId);
        if (pmc is null)
        {
            yield break;
        }

        if (pmc.Quests is not null)
        {
            foreach (var quest in pmc.Quests)
            {
                if (quest.Status == QuestStatusEnum.Success && data.QuestLocations.TryGetValue(quest.QId.ToString(), out var id))
                {
                    yield return id;
                }
            }
        }

        if (pmc.Hideout?.Areas is not null)
        {
            foreach (var area in pmc.Hideout.Areas)
            {
                var built = area.Level ?? 0;
                for (var level = 1; level <= built; level++)
                {
                    if (data.HideoutLocations.TryGetValue(((int)area.Type, level), out var id))
                    {
                        yield return id;
                    }
                }
            }
        }

        foreach (var (map, count) in state.ExtractCounts)
        {
            for (var n = 1; n <= count; n++)
            {
                if (data.ExtractLocations.TryGetValue((map, n), out var id))
                {
                    yield return id;
                }
            }
        }
    }

    public QuestStatusEnum? QuestState(string profileId, string questId)
    {
        var pmc = Pmc(profileId);
        return pmc?.Quests?.FirstOrDefault(q => q.QId.ToString() == questId)?.Status;
    }

    public bool QuestCompleted(string profileId, string questId)
    {
        var pmc = Pmc(profileId);
        return pmc?.Quests?.Any(q => q.QId.ToString() == questId && q.Status == QuestStatusEnum.Success) ?? false;
    }

    // ------------------------------------------------------------------ item effects
    public void AddExperience(string profileId, int amount)
    {
        var id = new MongoId(profileId);
        profileHelper.AddExperienceToPmc(id, amount);
        traderHelper.ValidateTraderStandingsAndPlayerLevelForProfile(id);
    }

    /// <summary>
    /// Change a trader's standing by <paramref name="amount"/> and return the change really made.
    /// (SPT's AddStandingToTrader floors every trader at 0, which would turn a small loss into a gain
    /// for a negative Fence standing.) Fence stays within SPT's configured range and the Scav copy follows.
    /// </summary>
    public double AddReputation(string profileId, string traderId, double amount)
    {
        var full = profileHelper.GetFullProfile(new MongoId(profileId));
        var pmc = full?.CharacterData?.PmcData;
        var trader = new MongoId(traderId);
        if (pmc?.TradersInfo is null || !pmc.TradersInfo.TryGetValue(trader, out var info))
        {
            return 0;
        }

        var before = info.Standing ?? 0;
        var after = before + amount;
        if (trader == Traders.FENCE)
        {
            var fence = FenceConfig();
            after = Math.Clamp(after, fence.Min, fence.Max);
            if (full!.CharacterData!.ScavData?.TradersInfo?.TryGetValue(trader, out var scavInfo) == true)
            {
                scavInfo.Standing = after;
            }
        }

        info.Standing = after;
        traderHelper.LevelUp(trader, pmc);
        return after - before;
    }

    private (double Min, double Max) FenceConfig()
    {
        try
        {
            var fence = traderConfig.Fence;
            return (fence.PlayerRepMin, fence.PlayerRepMax);
        }
        catch
        {
            return (-7, 15);
        }
    }

    public void ApplyTrap(string profileId, string trap, double amount, int seconds)
    {
        var pmc = Pmc(profileId);
        if (pmc is null)
        {
            return;
        }

        switch (trap)
        {
            // fence_rep goes through AddReputation (the caller needs the change actually made)
            case "exhaustion":
                CapEnergyAndHydration(pmc);
                break;
            case "scav_cooldown":
                // SPT keeps the Scav cooldown on the Scav profile. Add to whatever is left of it.
                ExtendScavLock(profileHelper.GetScavProfile(new MongoId(profileId)), seconds);
                break;
        }
    }

    /// <summary>Push the Scav's cooldown back by <paramref name="seconds"/> (from now if it is already over).</summary>
    public static void ExtendScavLock(PmcData? scav, int seconds)
    {
        if (scav?.Info is null || seconds <= 0)
        {
            return;
        }

        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        scav.Info.SavageLockTime = Math.Max(scav.Info.SavageLockTime ?? 0, now) + seconds;
    }

    /// <summary>
    /// Add server-side changes the client has not loaded yet into the profile the client sent at raid end,
    /// so SPT copying that profile does not undo them.
    /// </summary>
    public void AddPendingToRaidResult(PmcData? postRaid, ProfileState state, bool isPmc)
    {
        if (postRaid is null || !state.HasPendingSync)
        {
            return;
        }

        if (!isPmc)
        {
            // A Scav raid only carries Fence's standing back to the PMC, from the client's Scav copy.
            if (state.PendingScavFence != 0 && postRaid.TradersInfo is not null
                && postRaid.TradersInfo.TryGetValue(Traders.FENCE, out var scavFence))
            {
                scavFence.Standing = (scavFence.Standing ?? 0) + state.PendingScavFence;
            }

            return;
        }

        if (postRaid.Info is not null && state.PendingXp != 0)
        {
            postRaid.Info.Experience = (postRaid.Info.Experience ?? 0) + state.PendingXp;
        }

        if (postRaid.TradersInfo is not null)
        {
            foreach (var (traderId, delta) in state.PendingRep)
            {
                if (postRaid.TradersInfo.TryGetValue(new MongoId(traderId), out var info))
                {
                    info.Standing = (info.Standing ?? 0) + delta;
                }
            }
        }

        if (state.PendingExhaustion)
        {
            CapEnergyAndHydration(postRaid);
        }
    }

    private static void CapEnergyAndHydration(PmcData pmc)
    {
        if (pmc.Health?.Energy is { } energy)
        {
            energy.Current = Math.Min(energy.Current ?? 0, Math.Max(10, (energy.Maximum ?? 100) * 0.25));
        }

        if (pmc.Health?.Hydration is { } hydration)
        {
            hydration.Current = Math.Min(hydration.Current ?? 0, Math.Max(10, (hydration.Maximum ?? 100) * 0.25));
        }
    }

    /// <summary>Recompute player level and trader loyalty from experience/standing.</summary>
    public void RecalculateLevels(string profileId)
    {
        try
        {
            traderHelper.ValidateTraderStandingsAndPlayerLevelForProfile(new MongoId(profileId));
        }
        catch (Exception ex)
        {
            logger.Warning($"[Archipelago] could not recalculate levels for {profileId}: {ex.Message}");
        }
    }

    /// <summary>Drop the oldest Archipelago chat lines (never ones with uncollected items).</summary>
    public void TrimBotDialog(string profileId)
    {
        try
        {
            var full = profileHelper.GetFullProfile(new MongoId(profileId));
            if (full?.DialogueRecords is null || !full.DialogueRecords.TryGetValue(ChatBotId, out var dialog) || dialog.Messages is null)
            {
                return;
            }

            var excess = dialog.Messages.Count - MaxBotMessages;
            if (excess <= 0)
            {
                return;
            }

            var removable = dialog.Messages
                .Where(m => m.HasRewards != true || m.RewardCollected == true)
                .OrderBy(m => m.DateTime ?? 0)
                .Take(excess)
                .ToHashSet();
            dialog.Messages.RemoveAll(m => removable.Contains(m));
        }
        catch (Exception ex)
        {
            logger.Warning($"[Archipelago] could not trim chat for {profileId}: {ex.Message}");
        }
    }

    public List<Item> CreateMoney(string currencyTpl, int amount) => lootPools.CreateItem(new MongoId(currencyTpl), amount);

    public List<Item> CreateStartingKit(string? quality, Random rng, out List<string> contents) =>
        lootPools.CreateStartingKit(quality, rng, out contents);

    /// <summary>A profile nobody has played yet: no experience and no quest taken.</summary>
    public bool IsFreshProfile(string profileId)
    {
        var pmc = Pmc(profileId);
        if (pmc?.Info is null)
        {
            return false;
        }

        var played = pmc.Quests?.Any(q => q.Status is QuestStatusEnum.Started or QuestStatusEnum.AvailableForFinish
            or QuestStatusEnum.Success or QuestStatusEnum.Fail or QuestStatusEnum.FailRestartable) ?? false;
        return (pmc.Info.Experience ?? 0) <= 0 && !played;
    }

    // Equipment slots a "replace" starting kit leaves alone (their contents are still emptied for the containers).
    private static readonly HashSet<string> KeptEquipmentSlots = new(StringComparer.Ordinal)
    {
        "Scabbard", "SecuredContainer", "Pockets", "ArmBand", "Compass", "Dogtag", "SpecialSlot1", "SpecialSlot2", "SpecialSlot3",
    };

    /// <summary>
    /// Empty the starting stash and take off the starting gear (the knife, pockets, secure container and special
    /// slots stay, emptied). Only called just before the game client loads the profile, so the client never holds
    /// items the server no longer has. Returns how many top-level items were removed.
    /// </summary>
    public int ClearStartingInventory(string profileId)
    {
        var inventory = Pmc(profileId)?.Inventory;
        if (inventory?.Items is null || inventory.Stash is null || inventory.Equipment is null)
        {
            return 0;
        }

        var stash = inventory.Stash.Value.ToString();
        var equipment = inventory.Equipment.Value.ToString();
        var emptied = inventory.Items
            .Where(i => i.ParentId == equipment && i.SlotId is "SecuredContainer" or "Pockets")
            .Select(i => i.Id.ToString())
            .ToHashSet();

        var roots = inventory.Items.Where(i =>
                i.ParentId == stash
                || (i.ParentId == equipment && !KeptEquipmentSlots.Contains(i.SlotId ?? ""))
                || (i.ParentId != null && emptied.Contains(i.ParentId)))
            .Select(i => i.Id.ToString())
            .ToList();

        var children = inventory.Items.Where(i => i.ParentId != null).ToLookup(i => i.ParentId!);
        var removed = new HashSet<string>();
        var queue = new Queue<string>(roots);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!removed.Add(id))
            {
                continue;
            }

            foreach (var child in children[id])
            {
                queue.Enqueue(child.Id.ToString());
            }
        }

        inventory.Items.RemoveAll(i => removed.Contains(i.Id.ToString()));
        if (inventory.FastPanel is not null)
        {
            foreach (var key in inventory.FastPanel.Where(kv => removed.Contains(kv.Value.ToString())).Select(kv => kv.Key).ToList())
            {
                inventory.FastPanel.Remove(key);
            }
        }

        if (inventory.FavoriteItems is not null)
        {
            inventory.FavoriteItems = inventory.FavoriteItems.Where(f => !removed.Contains(f.ToString())).ToList();
        }

        return roots.Count;
    }

    public List<Item> CreateCrate(string pool, int count, Random rng, out string description)
    {
        var items = lootPools.CreateFromPool(pool, count, rng, out var picked);
        description = lootPools.Describe(picked);
        return items;
    }

    /// <summary>Write the SPT profile to disk. Returns false if that failed.</summary>
    public bool Save(string profileId)
    {
        try
        {
            saveServer.SaveProfileAsync(new MongoId(profileId)).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning($"[Archipelago] could not save profile {profileId}: {ex.Message}");
            return false;
        }
    }
}

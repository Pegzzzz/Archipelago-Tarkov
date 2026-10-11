using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArchipelagoTarkov.State;

/// <summary>An item received from the multiworld, in the order the AP server sent it.</summary>
public sealed class ReceivedItem
{
    [JsonPropertyName("item")] public long Item { get; set; }
    [JsonPropertyName("location")] public long Location { get; set; }
    [JsonPropertyName("player")] public int Player { get; set; }
    [JsonPropertyName("flags")] public int Flags { get; set; }
}

/// <summary>
/// Everything the mod remembers about one SPT profile's Archipelago game.
/// Saved to user/mods/ArchipelagoTarkov/profiles/&lt;profile id&gt;.json
/// </summary>
public sealed class ProfileState
{
    [JsonPropertyName("profileId")] public string ProfileId { get; set; } = "";

    // Connection
    [JsonPropertyName("server")] public string? Server { get; set; }
    [JsonPropertyName("slot")] public string? Slot { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("autoConnect")] public bool AutoConnect { get; set; } = true;

    // Multiworld identity (set on first successful connection)
    [JsonPropertyName("seedName")] public string? SeedName { get; set; }
    [JsonPropertyName("team")] public int Team { get; set; }
    [JsonPropertyName("slotId")] public int SlotId { get; set; }
    /// <summary>The slot this profile is bound to (set on the first successful connection).</summary>
    [JsonPropertyName("boundSlot")] public int? BoundSlot { get; set; }
    [JsonPropertyName("boundSlotName")] public string? BoundSlotName { get; set; }
    [JsonPropertyName("slotData")] public JsonElement? SlotData { get; set; }
    /// <summary>Every location id that belongs to this slot (missing + checked at connect time).</summary>
    [JsonPropertyName("slotLocations")] public HashSet<long> SlotLocations { get; set; } = new();

    // Items
    [JsonPropertyName("received")] public List<ReceivedItem> Received { get; set; } = new();
    /// <summary>How many of <see cref="Received"/> have had their one-off effects applied.</summary>
    [JsonPropertyName("delivered")] public int Delivered { get; set; }
    /// <summary>Indexes of received items whose effect waits until the player is out of raid.</summary>
    [JsonPropertyName("deferred")] public List<int> Deferred { get; set; } = new();

    // Checks
    [JsonPropertyName("checked")] public HashSet<long> Checked { get; set; } = new();
    [JsonPropertyName("extractCounts")] public Dictionary<string, int> ExtractCounts { get; set; } = new();
    [JsonPropertyName("goalSent")] public bool GoalSent { get; set; }
    [JsonPropertyName("collectorWarned")] public bool CollectorWarned { get; set; }

    // Random starting kit: "extra" or "replace" while it waits to be given, then StartingKitGiven.
    [JsonPropertyName("startingKitPending")] public string? StartingKitPending { get; set; }
    [JsonPropertyName("startingKitGiven")] public bool StartingKitGiven { get; set; }

    // Profile changes made by the server that the game client has not seen yet. SPT overwrites
    // experience, trader standing and health with the client's copy when a raid ends, so these are
    // added back into that copy until the client next receives the server's values (any inventory
    // action, or loading the profile).
    [JsonPropertyName("pendingXp")] public int PendingXp { get; set; }
    [JsonPropertyName("pendingRep")] public Dictionary<string, double> PendingRep { get; set; } = new();
    [JsonPropertyName("pendingExhaustion")] public bool PendingExhaustion { get; set; }
    // The client's Scav copy is only refreshed when the profile is loaded or the Scav is regenerated.
    [JsonPropertyName("pendingScavFence")] public double PendingScavFence { get; set; }
    [JsonPropertyName("pendingScavLockSeconds")] public int PendingScavLockSeconds { get; set; }

    [JsonIgnore] public bool HasPendingPmcSync => PendingXp != 0 || PendingRep.Count > 0 || PendingExhaustion;
    [JsonIgnore] public bool HasPendingSync => HasPendingPmcSync || PendingScavFence != 0 || PendingScavLockSeconds != 0;

    public void ClearPendingPmcSync()
    {
        PendingXp = 0;
        PendingRep.Clear();
        PendingExhaustion = false;
    }

    public void ClearPendingSync()
    {
        ClearPendingPmcSync();
        PendingScavFence = 0;
        PendingScavLockSeconds = 0;
    }

    [JsonIgnore] public bool IsConfigured => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Slot);
    [JsonIgnore] public bool HasSlotData => SlotData is { ValueKind: JsonValueKind.Object };

    public bool SlotFlag(string key, bool fallback = false)
    {
        if (!HasSlotData || !SlotData!.Value.TryGetProperty(key, out var v))
        {
            return fallback;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetInt32() != 0,
            _ => fallback,
        };
    }

    public int SlotInt(string key, int fallback = 0)
    {
        if (!HasSlotData || !SlotData!.Value.TryGetProperty(key, out var v))
        {
            return fallback;
        }

        return v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
    }

    public string? SlotString(string key)
    {
        if (!HasSlotData || !SlotData!.Value.TryGetProperty(key, out var v))
        {
            return null;
        }

        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>Forget everything tied to a multiworld (keeps the connection settings).</summary>
    public void ResetGame()
    {
        SeedName = null;
        Team = 0;
        SlotId = 0;
        BoundSlot = null;
        BoundSlotName = null;
        CollectorWarned = false;
        StartingKitPending = null;
        StartingKitGiven = false;
        // Pending* are kept: they describe the SPT profile, not the multiworld.
        SlotData = null;
        SlotLocations.Clear();
        Received.Clear();
        Delivered = 0;
        Deferred.Clear();
        Checked.Clear();
        ExtractCounts.Clear();
        GoalSent = false;
        AutoConnect = false;
    }
}

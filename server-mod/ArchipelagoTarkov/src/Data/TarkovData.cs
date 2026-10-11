using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArchipelagoTarkov.Data;

/// <summary>
/// The table shared with the APWorld (tools/extract_data.py -> tarkov_data.json).
/// Embedded in the mod so item and location IDs can never disagree with the APWorld.
/// </summary>
public sealed class TarkovData
{
    [JsonPropertyName("meta")] public DataMeta Meta { get; set; } = new();
    [JsonPropertyName("maps")] public List<MapDef> Maps { get; set; } = [];
    [JsonPropertyName("traders")] public List<TraderDef> Traders { get; set; } = [];
    [JsonPropertyName("collector_quest_id")] public string CollectorQuestId { get; set; } = "";
    [JsonPropertyName("quests")] public List<QuestDef> Quests { get; set; } = [];
    [JsonPropertyName("hideout")] public List<HideoutDef> Hideout { get; set; } = [];
    [JsonPropertyName("extracts")] public List<ExtractDef> Extracts { get; set; } = [];
    [JsonPropertyName("items")] public List<ItemDef> Items { get; set; } = [];

    [JsonIgnore] public Dictionary<long, ItemDef> ItemsById { get; private set; } = new();
    [JsonIgnore] public Dictionary<string, long> QuestLocations { get; private set; } = new();
    [JsonIgnore] public Dictionary<(int Area, int Level), long> HideoutLocations { get; private set; } = new();
    [JsonIgnore] public Dictionary<(string Map, int Count), long> ExtractLocations { get; private set; } = new();
    [JsonIgnore] public Dictionary<long, string> LocationNames { get; private set; } = new();
    [JsonIgnore] public HashSet<long> QuestLocationIds { get; private set; } = new();
    /// <summary>lower-case SPT location id (bigmap, sandbox_high, rezervbase...) -> map key</summary>
    [JsonIgnore] public Dictionary<string, string> MapKeyByLocationId { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public Dictionary<string, MapDef> MapsByKey { get; private set; } = new();
    [JsonIgnore] public Dictionary<string, TraderDef> TradersByKey { get; private set; } = new();
    [JsonIgnore] public Dictionary<string, TraderDef> TradersById { get; private set; } = new();
    /// <summary>Mechanic's "Introduction", whose reward normally unlocks Jaeger.</summary>
    [JsonIgnore] public string? JaegerUnlockQuestId { get; private set; }
    /// <summary>Therapist's "Colleagues - Part 3" and Jaeger's "The Huntsman Path - Sadist" (Kappa missable).</summary>
    [JsonIgnore] public string? ColleaguesPart3Id { get; private set; }
    [JsonIgnore] public string? SadistId { get; private set; }

    public static TarkovData LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("ArchipelagoTarkov.tarkov_data.json")
                           ?? throw new InvalidOperationException("tarkov_data.json is not embedded in ArchipelagoTarkov.dll");
        var data = JsonSerializer.Deserialize<TarkovData>(stream)
                   ?? throw new InvalidOperationException("tarkov_data.json could not be read");
        data.Index();
        return data;
    }

    public void Index()
    {
        ItemsById = Items.ToDictionary(i => i.Id);
        foreach (var q in Quests)
        {
            if (q.Check && q.LocationId is { } id)
            {
                QuestLocations[q.Id] = id;
                QuestLocationIds.Add(id);
                LocationNames[id] = q.LocationName ?? q.Name;
            }
        }

        foreach (var h in Hideout)
        {
            HideoutLocations[(h.Area, h.Level)] = h.LocationId;
            LocationNames[h.LocationId] = h.Name;
        }

        foreach (var e in Extracts)
        {
            ExtractLocations[(e.Map, e.Count)] = e.LocationId;
            LocationNames[e.LocationId] = e.Name;
        }

        foreach (var m in Maps)
        {
            MapsByKey[m.Key] = m;
            foreach (var loc in m.LocationIds)
            {
                MapKeyByLocationId[loc] = m.Key;
            }
        }

        foreach (var t in Traders)
        {
            TradersByKey[t.Key] = t;
            TradersById[t.Id] = t;
        }

        JaegerUnlockQuestId = Quests.FirstOrDefault(q => q.Name == "Introduction" && q.Trader == "mechanic")?.Id;
        ColleaguesPart3Id = Quests.FirstOrDefault(q => q.Name == "Colleagues - Part 3")?.Id;
        SadistId = Quests.FirstOrDefault(q => q.Name == "The Huntsman Path - Sadist")?.Id;
    }
}

public sealed class DataMeta
{
    [JsonPropertyName("spt_version")] public string SptVersion { get; set; } = "";
    [JsonPropertyName("eft_build")] public string EftBuild { get; set; } = "";
    [JsonPropertyName("item_base")] public long ItemBase { get; set; }
    [JsonPropertyName("location_base")] public long LocationBase { get; set; }
    [JsonPropertyName("data_hash")] public string DataHash { get; set; } = "";
}

public sealed class MapDef
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("location_ids")] public List<string> LocationIds { get; set; } = [];
    [JsonPropertyName("item")] public string Item { get; set; } = "";
}

public sealed class TraderDef
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
}

public sealed class QuestDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("trader")] public string? Trader { get; set; }
    [JsonPropertyName("check")] public bool Check { get; set; }
    [JsonPropertyName("location_id")] public long? LocationId { get; set; }
    [JsonPropertyName("location_name")] public string? LocationName { get; set; }
}

public sealed class HideoutDef
{
    [JsonPropertyName("area")] public int Area { get; set; }
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("location_id")] public long LocationId { get; set; }
}

public sealed class ExtractDef
{
    [JsonPropertyName("map")] public string Map { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("location_id")] public long LocationId { get; set; }
}

public sealed class ItemDef
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("classification")] public string Classification { get; set; } = "";
    [JsonPropertyName("params")] public JsonElement Params { get; set; }

    public string? Str(string key) =>
        Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public double Num(string key, double fallback = 0) =>
        Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : fallback;
}

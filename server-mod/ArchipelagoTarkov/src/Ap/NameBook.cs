using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArchipelagoTarkov.Ap;

/// <summary>Resolves player, item and location ids to names using the room's data package.</summary>
public sealed class NameBook
{
    private readonly string _cacheDir;
    private readonly Dictionary<int, string> _playerNames = new();
    private readonly Dictionary<int, string> _playerGames = new();
    private readonly Dictionary<string, Dictionary<long, string>> _items = new();
    private readonly Dictionary<string, Dictionary<long, string>> _locations = new();
    private readonly Dictionary<string, string> _checksums = new();

    public NameBook(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public void SetPlayers(JsonArray? players, JsonObject? slotInfo)
    {
        if (players != null)
        {
            foreach (var p in players.OfType<JsonObject>())
            {
                var slot = p["slot"]?.GetValue<int>() ?? -1;
                var alias = p["alias"]?.GetValue<string>();
                var name = p["name"]?.GetValue<string>();
                if (slot >= 0)
                {
                    _playerNames[slot] = string.IsNullOrWhiteSpace(alias) ? name ?? $"Player {slot}" : alias!;
                }
            }
        }

        if (slotInfo != null)
        {
            foreach (var (key, value) in slotInfo)
            {
                if (int.TryParse(key, out var slot) && value is JsonObject info)
                {
                    var game = info["game"]?.GetValue<string>();
                    if (game != null)
                    {
                        _playerGames[slot] = game;
                    }

                    if (!_playerNames.ContainsKey(slot) && info["name"]?.GetValue<string>() is { } name)
                    {
                        _playerNames[slot] = name;
                    }
                }
            }
        }
    }

    public string Player(int slot) => slot == 0 ? "Archipelago" : _playerNames.TryGetValue(slot, out var n) ? n : $"Player {slot}";

    public string? GameOf(int slot) => _playerGames.TryGetValue(slot, out var g) ? g : null;

    public string Item(long id, int ownerSlot)
    {
        var game = GameOf(ownerSlot);
        if (game != null && _items.TryGetValue(game, out var table) && table.TryGetValue(id, out var name))
        {
            return name;
        }

        foreach (var t in _items.Values)
        {
            if (t.TryGetValue(id, out var any))
            {
                return any;
            }
        }

        return $"Item {id}";
    }

    public string Location(long id, int ownerSlot)
    {
        if (id == -1)
        {
            return "a server command";
        }

        if (id == -2)
        {
            return "starting inventory";
        }

        var game = GameOf(ownerSlot);
        if (game != null && _locations.TryGetValue(game, out var table) && table.TryGetValue(id, out var name))
        {
            return name;
        }

        foreach (var t in _locations.Values)
        {
            if (t.TryGetValue(id, out var any))
            {
                return any;
            }
        }

        return $"Location {id}";
    }

    /// <summary>Games whose names still need fetching (not in memory and not in the disk cache).</summary>
    public List<string> MissingGames(JsonObject? checksums)
    {
        var missing = new List<string>();
        if (checksums == null)
        {
            return missing;
        }

        foreach (var (game, value) in checksums)
        {
            var checksum = value?.GetValue<string>() ?? "";
            if (_items.ContainsKey(game) && _checksums.GetValueOrDefault(game) == checksum && checksum != "")
            {
                continue;
            }

            if (!TryLoadCache(game, checksum))
            {
                missing.Add(game);
            }
        }

        return missing;
    }

    public void AddDataPackage(JsonObject? games)
    {
        if (games == null)
        {
            return;
        }

        foreach (var (game, value) in games)
        {
            if (value is not JsonObject gameData)
            {
                continue;
            }

            var checksum = gameData["checksum"]?.GetValue<string>();
            Load(game, gameData, checksum);
            if (!string.IsNullOrEmpty(checksum))
            {
                try
                {
                    File.WriteAllText(CachePath(game, checksum), gameData.ToJsonString());
                }
                catch
                {
                    // cache is optional
                }
            }
        }
    }

    private void Load(string game, JsonObject gameData, string? checksum)
    {
        if (!string.IsNullOrEmpty(checksum))
        {
            _checksums[game] = checksum;
        }

        var items = new Dictionary<long, string>();
        if (gameData["item_name_to_id"] is JsonObject itemMap)
        {
            foreach (var (name, id) in itemMap)
            {
                if (id != null)
                {
                    items[id.GetValue<long>()] = name;
                }
            }
        }

        var locations = new Dictionary<long, string>();
        if (gameData["location_name_to_id"] is JsonObject locMap)
        {
            foreach (var (name, id) in locMap)
            {
                if (id != null)
                {
                    locations[id.GetValue<long>()] = name;
                }
            }
        }

        _items[game] = items;
        _locations[game] = locations;
    }

    private string CachePath(string game, string checksum)
    {
        var safe = string.Concat(game.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        var safeSum = string.Concat(checksum.Where(char.IsLetterOrDigit));
        return Path.Combine(_cacheDir, $"{safe}-{safeSum}.json");
    }

    private bool TryLoadCache(string game, string checksum)
    {
        if (string.IsNullOrEmpty(checksum))
        {
            return false;
        }

        var path = CachePath(game, checksum);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj)
            {
                Load(game, obj, checksum);
                return true;
            }
        }
        catch (JsonException)
        {
            // fall through and refetch
        }

        return false;
    }
}

using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Cloners;

namespace ArchipelagoTarkov.Game;

/// <summary>
/// Builds the actual Tarkov items behind "crate" Archipelago items (Weapon Crate, Medical Crate...) and the
/// random starting kit. Pools are computed once from the loaded item database, so items added by other mods
/// are included as long as they have a handbook price and are not blacklisted.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class LootPools(
    DatabaseService databaseService,
    ItemHelper itemHelper,
    PresetHelper presetHelper,
    HandbookHelper handbookHelper,
    ItemFilterService itemFilterService,
    ICloner cloner
)
{
    private sealed record Candidate(MongoId Tpl, double Price, TemplateItem Template, int ArmorClass = 0);

    private sealed class Catalog
    {
        public readonly Dictionary<string, List<MongoId>> Pools = new()
        {
            ["weapon"] = [],
            ["armor"] = [],
            ["medical"] = [],
            ["ammo"] = [],
            ["barter"] = [],
            ["provisions"] = [],
            ["key"] = [],
            ["stim"] = [],
        };

        public readonly List<Candidate> Weapons = [];
        public readonly List<Candidate> BodyArmor = [];
        public readonly List<Candidate> Helmets = [];
        public readonly List<Candidate> Rigs = [];
        public readonly List<Candidate> Backpacks = [];
        public readonly List<Candidate> Meds = [];
        public readonly List<Candidate> Food = [];
        public readonly Dictionary<string, List<Candidate>> AmmoByCaliber = new(StringComparer.Ordinal);
        public readonly Dictionary<MongoId, Candidate> Magazines = new();
    }

    private Catalog? _catalog;
    private readonly object _lock = new();

    private Catalog Data
    {
        get
        {
            lock (_lock)
            {
                return _catalog ??= Build();
            }
        }
    }

    public int PoolSize(string pool) => Data.Pools.TryGetValue(pool, out var list) ? list.Count : 0;

    private Catalog Build()
    {
        var catalog = new Catalog();
        var pools = catalog.Pools;
        foreach (var (tpl, template) in databaseService.GetItems())
        {
            if (template.Type != "Item" || template.Properties is null)
            {
                continue;
            }

            var props = template.Properties;
            if (props.QuestItem == true || itemFilterService.IsItemBlacklisted(tpl) || itemFilterService.IsItemRewardBlacklisted(tpl)
                || itemFilterService.IsBossItem(tpl))
            {
                continue;
            }

            double price;
            try
            {
                price = handbookHelper.GetTemplatePrice(tpl);
            }
            catch
            {
                continue;
            }

            if (price <= 1)
            {
                continue;
            }

            if (itemHelper.IsOfBaseclass(tpl, BaseClasses.WEAPON))
            {
                if (!itemHelper.IsOfBaseclass(tpl, BaseClasses.SPECIAL_WEAPON) && presetHelper.GetDefaultPreset(tpl) is not null
                    && price is >= 5_000 and <= 200_000)
                {
                    pools["weapon"].Add(tpl);
                    catalog.Weapons.Add(new Candidate(tpl, price, template));
                }
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.ARMOR) || itemHelper.IsOfBaseclass(tpl, BaseClasses.VEST)
                     || itemHelper.IsOfBaseclass(tpl, BaseClasses.HEADWEAR))
            {
                // Since SPT 4.0 the protection lives in the plates and soft inserts, not on the vest itself.
                var armorClass = EffectiveArmorClass(tpl, template);
                if (armorClass is > 0 and <= 5 && price <= 250_000)
                {
                    pools["armor"].Add(tpl);
                    var candidate = new Candidate(tpl, price, template, armorClass);
                    if (itemHelper.IsOfBaseclass(tpl, BaseClasses.HEADWEAR))
                    {
                        catalog.Helmets.Add(candidate);
                    }
                    else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.ARMOR))
                    {
                        catalog.BodyArmor.Add(candidate);
                    }
                }
                else if (armorClass == 0 && itemHelper.IsOfBaseclass(tpl, BaseClasses.VEST) && !(props.Slots?.Any() ?? false)
                         && props.BlocksArmorVest != true)
                {
                    catalog.Rigs.Add(new Candidate(tpl, price, template));
                }
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.BACKPACK))
            {
                catalog.Backpacks.Add(new Candidate(tpl, price, template));
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.STIMULATOR))
            {
                pools["stim"].Add(tpl);
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.MEDS))
            {
                pools["medical"].Add(tpl);
                catalog.Meds.Add(new Candidate(tpl, price, template));
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.AMMO_BOX))
            {
                pools["ammo"].Add(tpl);
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.AMMO))
            {
                if (!string.IsNullOrEmpty(props.Caliber))
                {
                    if (!catalog.AmmoByCaliber.TryGetValue(props.Caliber, out var list))
                    {
                        catalog.AmmoByCaliber[props.Caliber] = list = [];
                    }

                    list.Add(new Candidate(tpl, price, template));
                }
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.MAGAZINE))
            {
                catalog.Magazines[tpl] = new Candidate(tpl, price, template);
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.FOOD_DRINK))
            {
                pools["provisions"].Add(tpl);
                catalog.Food.Add(new Candidate(tpl, price, template));
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.KEY_MECHANICAL) || itemHelper.IsOfBaseclass(tpl, BaseClasses.KEYCARD))
            {
                pools["key"].Add(tpl);
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.BARTER_ITEM) && price >= 5_000)
            {
                pools["barter"].Add(tpl);
            }
        }

        return catalog;
    }

    /// <summary>The best armor class among the item and the plates/inserts of its default preset.</summary>
    private int EffectiveArmorClass(MongoId tpl, TemplateItem template)
    {
        var best = template.Properties?.ArmorClass ?? 0;
        var preset = presetHelper.GetDefaultPreset(tpl);
        if (preset?.Items is null)
        {
            return best;
        }

        foreach (var part in preset.Items)
        {
            var partTemplate = itemHelper.GetItem(part.Template);
            if (partTemplate.Key && partTemplate.Value?.Properties?.ArmorClass is { } armorClass && armorClass > best)
            {
                best = armorClass;
            }
        }

        return best;
    }

    /// <summary>Create the item tree(s) for <paramref name="count"/> random picks from a pool.</summary>
    public List<Item> CreateFromPool(string pool, int count, Random rng, out List<MongoId> picked)
    {
        picked = [];
        var result = new List<Item>();
        if (!Data.Pools.TryGetValue(pool, out var list) || list.Count == 0)
        {
            return result;
        }

        for (var i = 0; i < count; i++)
        {
            var tpl = list[rng.Next(list.Count)];
            picked.Add(tpl);
            result.AddRange(CreateItem(tpl, 1));
        }

        return result;
    }

    // ------------------------------------------------------------------ starting kit
    private static readonly (double Min, double Max)[] WeaponPrice = [(5_000, 35_000), (25_000, 90_000), (70_000, 200_000)];
    private static readonly int[] BodyArmorClass = [2, 3, 4];
    private static readonly double[] HelmetChance = [0, 0.5, 1];
    private static readonly int[] Rounds = [90, 150, 240];
    private static readonly int[] SpareMagazines = [1, 2, 3];
    private static readonly int[] MedsCount = [2, 3, 5];
    private static readonly int[] FoodCount = [1, 2, 2];
    private static readonly (int Min, int Max)[] RoubleRange = [(30, 80), (100, 250), (300, 600)]; // thousands

    public static int QualityTier(string? quality) => quality switch
    {
        "scrappy" => 0,
        "geared" => 2,
        _ => 1,
    };

    /// <summary>
    /// A random starting kit: a weapon with spare magazines and matching ammo, body armor, maybe a helmet, a rig,
    /// a backpack, meds, food and roubles. Every piece is optional: whatever the database can't supply is skipped.
    /// </summary>
    public List<Item> CreateStartingKit(string? quality, Random rng, out List<string> contents)
    {
        var tier = QualityTier(quality);
        var data = Data;
        var items = new List<Item>();
        contents = [];

        // Weapon, magazines, ammo
        var weapon = PickInBand(data.Weapons, WeaponPrice[tier], rng) ?? PickAny(data.Weapons, rng);
        if (weapon is not null)
        {
            items.AddRange(CreateItem(weapon.Tpl, 1));
            contents.Add(Name(weapon.Tpl));

            var magazine = PickMagazine(data, weapon, rng);
            if (magazine is not null)
            {
                for (var i = 0; i < SpareMagazines[tier]; i++)
                {
                    items.AddRange(CreateItem(magazine.Tpl, 1));
                }

                contents.Add($"{SpareMagazines[tier]}x {Name(magazine.Tpl)}");
            }

            var caliber = weapon.Template.Properties?.AmmoCaliber;
            if (caliber is not null && data.AmmoByCaliber.TryGetValue(caliber, out var ammoList) && ammoList.Count > 0)
            {
                var ammo = PickByRank(ammoList, a => a.Template.Properties?.PenetrationPower ?? 0, tier, rng);
                items.AddRange(CreateItem(ammo.Tpl, Rounds[tier]));
                contents.Add($"{Rounds[tier]} rounds of {Name(ammo.Tpl)}");
            }
        }

        // Body armor of the tier's class (or the closest class there is)
        var armor = PickArmor(data.BodyArmor, BodyArmorClass[tier], rng);
        if (armor is not null)
        {
            items.AddRange(CreateItem(armor.Tpl, 1));
            contents.Add($"{Name(armor.Tpl)} (class {armor.ArmorClass})");
        }

        if (rng.NextDouble() < HelmetChance[tier])
        {
            var helmet = PickArmor(data.Helmets, tier + 1, rng);
            if (helmet is not null)
            {
                items.AddRange(CreateItem(helmet.Tpl, 1));
                contents.Add($"{Name(helmet.Tpl)} (class {helmet.ArmorClass})");
            }
        }

        foreach (var list in new[] { data.Rigs, data.Backpacks })
        {
            if (list.Count > 0)
            {
                var piece = PickByRank(list, c => c.Price, tier, rng);
                items.AddRange(CreateItem(piece.Tpl, 1));
                contents.Add(Name(piece.Tpl));
            }
        }

        AddSome(data.Meds, MedsCount[tier], tier, rng, items, contents);
        AddSome(data.Food, FoodCount[tier], 1, rng, items, contents);

        var roubles = rng.Next(RoubleRange[tier].Min, RoubleRange[tier].Max + 1) * 1000;
        items.AddRange(CreateItem(Money.ROUBLES, roubles));
        contents.Add($"{roubles:N0} roubles");

        return items;
    }

    private Candidate? PickMagazine(Catalog data, Candidate weapon, Random rng)
    {
        var props = weapon.Template.Properties;
        if (props?.ReloadMode != ReloadMode.ExternalMagazine || props.Slots is null)
        {
            return null;
        }

        var slot = props.Slots.FirstOrDefault(s => s.Name == "mod_magazine");
        var filter = slot?.Properties?.Filters?.FirstOrDefault()?.Filter;
        if (filter is null)
        {
            return null;
        }

        var options = filter.Where(data.Magazines.ContainsKey).Select(t => data.Magazines[t]).OrderBy(m => m.Tpl.ToString()).ToList();
        return options.Count == 0 ? null : options[rng.Next(options.Count)];
    }

    private void AddSome(List<Candidate> list, int count, int tier, Random rng, List<Item> items, List<string> contents)
    {
        if (list.Count == 0)
        {
            return;
        }

        var names = new List<MongoId>();
        for (var i = 0; i < count; i++)
        {
            var pick = PickByRank(list, c => c.Price, tier, rng);
            items.AddRange(CreateItem(pick.Tpl, 1));
            names.Add(pick.Tpl);
        }

        contents.Add(Describe(names));
    }

    private static Candidate? PickAny(List<Candidate> list, Random rng) => list.Count == 0 ? null : Sorted(list)[rng.Next(list.Count)];

    private static Candidate? PickInBand(List<Candidate> list, (double Min, double Max) band, Random rng)
    {
        var options = Sorted(list).Where(c => c.Price >= band.Min && c.Price <= band.Max).ToList();
        return options.Count == 0 ? null : options[rng.Next(options.Count)];
    }

    /// <summary>Armor of exactly <paramref name="armorClass"/>, else the closest class available.</summary>
    private static Candidate? PickArmor(List<Candidate> list, int armorClass, Random rng)
    {
        if (list.Count == 0)
        {
            return null;
        }

        var closest = list.Min(c => Math.Abs(c.ArmorClass - armorClass));
        var options = Sorted(list).Where(c => Math.Abs(c.ArmorClass - armorClass) == closest).ToList();
        return options[rng.Next(options.Count)];
    }

    /// <summary>Sort by <paramref name="key"/> and pick at random from the lower, middle or upper third.</summary>
    private static Candidate PickByRank(List<Candidate> list, Func<Candidate, double> key, int tier, Random rng)
    {
        var sorted = list.OrderBy(key).ThenBy(c => c.Tpl.ToString()).ToList();
        var third = Math.Max(1, sorted.Count / 3);
        var start = Math.Min(tier * third, Math.Max(0, sorted.Count - third));
        var end = tier == 2 ? sorted.Count : Math.Min(sorted.Count, start + third);
        return sorted[rng.Next(start, Math.Max(start + 1, end))];
    }

    // Database order isn't guaranteed; sort so the same seed always rolls the same kit.
    private static List<Candidate> Sorted(List<Candidate> list) => list.OrderBy(c => c.Tpl.ToString()).ToList();

    private string Name(MongoId tpl) => itemHelper.GetItemName(tpl) ?? tpl.ToString();

    /// <summary>Create one item (with its default preset if it has one) or a stack.</summary>
    public List<Item> CreateItem(MongoId tpl, int stackCount)
    {
        var preset = presetHelper.GetDefaultPreset(tpl);
        if (preset is not null)
        {
            var items = cloner.Clone(preset.Items)!.ReplaceIDs().ToList();
            foreach (var item in items.Where(x => x.ParentId is null))
            {
                item.SlotId = null;
            }

            return items;
        }

        var template = itemHelper.GetItem(tpl);
        if (!template.Key || template.Value is null)
        {
            return [];
        }

        var root = new Item
        {
            Id = new MongoId(),
            Template = tpl,
            Upd = itemHelper.GenerateUpdForItem(template.Value),
        };

        if (itemHelper.IsOfBaseclass(tpl, BaseClasses.AMMO_BOX) || (template.Value.Properties?.StackMaxSize ?? 1) <= 1 || stackCount <= 1)
        {
            return [root];
        }

        root.Upd ??= new Upd();
        root.Upd.StackObjectsCount = stackCount;
        return itemHelper.SplitStack(root);
    }

    public string Describe(IEnumerable<MongoId> tpls)
    {
        var names = tpls.Select(t => itemHelper.GetItemName(t) ?? t.ToString()).ToList();
        return string.Join(", ", names.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
    }
}

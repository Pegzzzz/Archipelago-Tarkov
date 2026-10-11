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
/// Builds the actual Tarkov items behind "crate" Archipelago items (Weapon Crate, Medical Crate...).
/// Pools are computed once from the loaded item database, so items added by other mods are included
/// as long as they have a handbook price and are not blacklisted.
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
    private Dictionary<string, List<MongoId>>? _pools;
    private readonly object _lock = new();

    private Dictionary<string, List<MongoId>> Pools
    {
        get
        {
            lock (_lock)
            {
                return _pools ??= Build();
            }
        }
    }

    public int PoolSize(string pool) => Pools.TryGetValue(pool, out var list) ? list.Count : 0;

    private Dictionary<string, List<MongoId>> Build()
    {
        var pools = new Dictionary<string, List<MongoId>>
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
                }
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.ARMOR) || itemHelper.IsOfBaseclass(tpl, BaseClasses.VEST)
                     || itemHelper.IsOfBaseclass(tpl, BaseClasses.HEADWEAR))
            {
                if ((props.ArmorClass ?? 0) is > 0 and <= 5 && price <= 250_000)
                {
                    pools["armor"].Add(tpl);
                }
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.STIMULATOR))
            {
                pools["stim"].Add(tpl);
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.MEDS))
            {
                pools["medical"].Add(tpl);
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.AMMO_BOX))
            {
                pools["ammo"].Add(tpl);
            }
            else if (itemHelper.IsOfBaseclass(tpl, BaseClasses.FOOD_DRINK))
            {
                pools["provisions"].Add(tpl);
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

        return pools;
    }

    /// <summary>Create the item tree(s) for <paramref name="count"/> random picks from a pool.</summary>
    public List<Item> CreateFromPool(string pool, int count, Random rng, out List<MongoId> picked)
    {
        picked = [];
        var result = new List<Item>();
        if (!Pools.TryGetValue(pool, out var list) || list.Count == 0)
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

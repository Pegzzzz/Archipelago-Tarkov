using System.Reflection;
using ArchipelagoTarkov.Session;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Routers;
using LocationPath = SPTarkov.Server.Core.Models.Eft.Common.Tables.Path;

namespace ArchipelagoTarkov.Patches;

/// <summary>
/// All patches are small postfixes/prefixes that only act on profiles linked to an Archipelago game,
/// so other mods patching the same methods keep working. Each patch is installed on its own: if one
/// target changed in a future SPT build, the others still load.
///
/// Other mods can replace an SPT class with a subclass (DI type overrides, e.g. SVM's ProfileController).
/// When they do, the method actually called is the override, so that is the one patched. Patch methods
/// take arguments by position (__0, __1...) because an override may rename its parameters.
/// </summary>
public static class PatchInstaller
{
    private static IReadOnlyDictionary<Type, Type> _runtimeTypes = new Dictionary<Type, Type>();

    /// <summary>SPT classes this mod patches; their DI-resolved types are looked up before patching.</summary>
    public static readonly Type[] PatchedTypes =
    [
        typeof(LocationController), typeof(TraderHelper), typeof(LocationLifecycleService),
        typeof(ProfileCallbacks), typeof(EventOutputHolder), typeof(ProfileController), typeof(GameCallbacks),
    ];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> Ready = new();

    /// <summary>True once every part (prefix, postfix, finalizer) of that patch is attached.</summary>
    internal static bool IsReady(Type patchType) => Ready.ContainsKey(patchType);

    /// <summary>The method that runs when SPT calls <paramref name="baseType"/>.<paramref name="name"/>.</summary>
    internal static MethodBase? Target(Type baseType, string name)
    {
        var baseMethod = AccessTools.Method(baseType, name);
        if (baseMethod is null || !_runtimeTypes.TryGetValue(baseType, out var actual))
        {
            return baseMethod;
        }

        var parameters = baseMethod.GetParameters().Select(p => p.ParameterType).ToArray();
        // Most derived declaration with the same signature (an override, or the base method if not overridden).
        for (var type = actual; type != null && type != baseType; type = type.BaseType)
        {
            var declared = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, parameters, null);
            if (declared != null && declared.GetBaseDefinition().DeclaringType == baseMethod.GetBaseDefinition().DeclaringType)
            {
                return declared;
            }
        }

        return baseMethod;
    }

    public static void InstallAll(Action<string> log, IReadOnlyDictionary<Type, Type>? runtimeTypes = null)
    {
        _runtimeTypes = runtimeTypes ?? new Dictionary<Type, Type>();
        AbstractPatch[] patches =
        [
            new LocationListPatch(),
            new TraderUnlockPatch(),
            new RaidStartPatch(),
            new RaidEndPatch(),
            new ProfileListPatch(),
            new ItemEventSyncPatch(),
            new ScavRegeneratePatch(),
            new GameStartPatch(),
        ];

        foreach (var patch in patches)
        {
            try
            {
                patch.Enable();
                Ready[patch.GetType()] = true;
                if (patch.TargetMethod?.DeclaringType is { } declaring && !declaring.Assembly.Equals(typeof(LocationController).Assembly))
                {
                    log($"[Archipelago] {patch.GetType().Name} attached to {declaring.FullName} ({declaring.Assembly.GetName().Name}).");
                }
            }
            catch (Exception ex)
            {
                log($"[Archipelago] could not install {patch.GetType().Name}: {(ex.InnerException ?? ex)}");
                try
                {
                    // Enable attaches prefix, postfix and finalizer one by one; never leave half a patch behind.
                    Harmony.UnpatchID(patch.GetType().Name);
                }
                catch
                {
                    // nothing more to do
                }
            }
        }
    }
}

/// <summary>/client/locations: hide maps the player has not unlocked, and transit paths that lead to them.</summary>
public class LocationListPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(LocationController), nameof(LocationController.GenerateAll));

    [PatchPostfix]
    public static void Postfix(MongoId __0, ref LocationsGenerateAllResponse __result)
    {
        var manager = SessionManager.Instance;
        var locked = manager?.LockedMaps(__0.ToString());
        if (manager is null || locked is null || locked.Count == 0 || __result?.Locations is null)
        {
            return;
        }

        // The response holds the live database objects: copy instead of editing them.
        var lockedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locations = new Dictionary<MongoId, LocationBase>();
        foreach (var (id, location) in __result.Locations)
        {
            var key = manager.MapKeyFor(location.Id);
            if (key != null && locked.Contains(key))
            {
                locations[id] = location with { Locked = true, Enabled = false, Transits = DisableTransits(location.Transits, manager, locked) };
                lockedIds.Add(id.ToString());
            }
            else
            {
                locations[id] = location with { Transits = DisableTransits(location.Transits, manager, locked) };
            }
        }

        var paths = __result.Paths?
            .Where(p => !lockedIds.Contains(p.Source ?? "") && !lockedIds.Contains(p.Destination ?? ""))
            .ToList();

        __result = new LocationsGenerateAllResponse { Locations = locations, Paths = paths ?? new List<LocationPath>() };
    }

    internal static IEnumerable<Transit>? DisableTransits(IEnumerable<Transit>? transits, SessionManager manager, HashSet<string> locked)
    {
        if (transits is null)
        {
            return null;
        }

        return transits.Select(t =>
        {
            var target = manager.MapKeyFor(t.Location);
            return target != null && locked.Contains(target) ? t with { IsActive = false } : t;
        }).ToList();
    }
}

/// <summary>Quest rewards that unlock a trader (Jaeger) must not bypass the trader item.</summary>
public class TraderUnlockPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(TraderHelper), nameof(TraderHelper.SetTraderUnlockedState));

    [PatchPrefix]
    public static bool Prefix(MongoId __0, bool __1, MongoId __2)
    {
        // (traderId, status, sessionId)
        if (!__1)
        {
            return true;
        }

        var manager = SessionManager.Instance;
        return manager is null || !manager.BlocksTraderUnlock(__2.ToString(), __0.ToString());
    }
}

/// <summary>client/match/local/start: remember the raid and switch off transits to locked maps.</summary>
public class RaidStartPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaidAsync));

    // StartLocalRaidAsync is async: a postfix runs when it returns its task, not when the raid data is ready.
    // So the task is replaced by one that waits for SPT's and then does our part.
    [PatchPostfix]
    public static void Postfix(MongoId __0, StartLocalRaidRequestData __1, ref Task<StartLocalRaidResponseData> __result)
    {
        if (SessionManager.Instance?.Get(__0.ToString()) is null || __result is null)
        {
            return;
        }

        __result = After(__result, __0, __1);
    }

    private static async Task<StartLocalRaidResponseData> After(Task<StartLocalRaidResponseData> original, MongoId sessionId,
        StartLocalRaidRequestData request)
    {
        var result = await original.ConfigureAwait(false);
        try
        {
            var manager = SessionManager.Instance;
            var session = manager?.Get(sessionId.ToString());
            if (manager is null || session is null)
            {
                return result;
            }

            var mapKey = manager.MapKeyFor(request?.Location);
            lock (session)
            {
                session.OnRaidStart(mapKey);
            }

            var locked = manager.LockedMaps(sessionId.ToString());
            if (locked is { Count: > 0 } && result?.LocationLoot is not null)
            {
                // Copy before editing: other mods (Fika co-op) can share one location object between players.
                result.LocationLoot = result.LocationLoot with
                {
                    Transits = LocationListPatch.DisableTransits(result.LocationLoot.Transits, manager, locked),
                };
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"raid start: {ex}");
        }

        return result;
    }
}

/// <summary>client/match/local/end: keep server-side changes the client has not seen, count extractions, send DeathLink.</summary>
public class RaidEndPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(LocationLifecycleService), nameof(LocationLifecycleService.EndLocalRaidAsync));

    internal static (string? MapKey, bool IsPmc) ParseServerId(SessionManager manager, string? serverId)
    {
        // "<location>.<pmc|scav>..." e.g. "bigmap.PMC.2025..." (the same parsing SPT does)
        var details = (serverId ?? "").Split('.');
        var mapKey = manager.MapKeyFor(details.Length > 0 ? details[0] : null);
        var isPmc = details.Length > 1 && details[1].Contains("pmc", StringComparison.OrdinalIgnoreCase);
        return (mapKey, isPmc);
    }

    /// <summary>
    /// Runs before SPT copies the client's profile in. The session stays "in raid" until the raid end has finished,
    /// so no Archipelago item changes the profile in between (they wait, as they do during a raid).
    /// </summary>
    [PatchPrefix]
    public static void Prefix(MongoId __0, EndLocalRaidRequestData __1)
    {
        try
        {
            var manager = SessionManager.Instance;
            var session = manager?.Get(__0.ToString());
            if (manager is null || session is null || __1?.Results?.Profile is null)
            {
                return;
            }

            var (_, isPmc) = ParseServerId(manager, __1.ServerId);
            lock (session)
            {
                session.BeforeRaidEnd(__1.Results.Profile, isPmc);
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"raid end (before): {ex}");
        }
    }

    [PatchPostfix]
    public static void Postfix(MongoId __0, EndLocalRaidRequestData __1, ref Task __result)
    {
        if (SessionManager.Instance?.Get(__0.ToString()) is null || __result is null)
        {
            return;
        }

        __result = After(__result, __0, __1);
    }

    private static async Task After(Task original, MongoId sessionId, EndLocalRaidRequestData request)
    {
        await original.ConfigureAwait(false);
        try
        {
            var manager = SessionManager.Instance;
            var session = manager?.Get(sessionId.ToString());
            if (manager is null || session is null || request?.Results is null)
            {
                return;
            }

            var (mapKey, isPmc) = ParseServerId(manager, request.ServerId);
            var killer = request.Results.Profile?.Stats?.Eft?.Aggressor?.Name;
            lock (session)
            {
                session.OnRaidEnd(mapKey, isPmc, request.Results.Result, killer);
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"raid end: {ex}");
        }
    }
}

/// <summary>
/// client/game/profile/list: the client loads the whole profile. SPT 4.1 streams the response after this method
/// returns, so instead of holding a lock: forget the pending changes now (the client is about to get every
/// server-side value) and hold back new profile changes for a few seconds while the response is written.
/// </summary>
public class ProfileListPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(ProfileCallbacks), nameof(ProfileCallbacks.GetProfileData));

    [PatchPrefix]
    public static void Prefix(MongoId __2)
    {
        // (url, request, sessionID)
        try
        {
            var session = SessionManager.Instance?.Get(__2.ToString());
            if (session is null)
            {
                return;
            }

            lock (session)
            {
                session.BeforeClientLoadsProfiles();
                session.OnClientLoadedProfiles();
                session.PauseProfileChanges(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"profile list: {ex}");
        }
    }
}

/// <summary>
/// Every inventory/trade/quest action returns the PMC's experience, health and trader standings to the
/// client, so pending changes are known to the client from then on.
/// </summary>
public class ItemEventSyncPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(EventOutputHolder), nameof(EventOutputHolder.UpdateOutputProperties));

    [PatchPrefix]
    public static void Prefix(MongoId __0, ref ApSession? __state)
    {
        __state = SessionLock.Enter(__0, typeof(ItemEventSyncPatch));
    }

    [PatchFinalizer]
    public static void Finalizer(Exception? __exception, ApSession? __state)
    {
        SessionLock.Exit(__state, __exception is null ? s => s.OnClientSyncedPmc() : null);
    }
}

/// <summary>client/game/profile/savage/regenerate: carry a Scav cooldown trap over to the new Scav.</summary>
public class ScavRegeneratePatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(ProfileController), nameof(ProfileController.GeneratePlayerScav));

    [PatchPrefix]
    public static void Prefix(MongoId __0, ref ApSession? __state)
    {
        // Held until the finalizer: a Fence Grudge landing mid-generation would otherwise miss the new Scav.
        __state = SessionLock.Enter(__0, typeof(ScavRegeneratePatch));
    }

    [PatchPostfix]
    public static void Postfix(MongoId __0, PmcData __result)
    {
        try
        {
            var session = SessionManager.Instance?.Get(__0.ToString());
            if (session is null)
            {
                return;
            }

            lock (session)
            {
                session.OnScavRegenerated(__result);
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"scav regenerate: {ex}");
        }
    }

    [PatchFinalizer]
    public static void Finalizer(ApSession? __state)
    {
        SessionLock.Exit(__state, null);
    }
}

/// <summary>client/game/start: the game was (re)started, so the player is not in a raid any more.</summary>
public class GameStartPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(GameCallbacks), nameof(GameCallbacks.GameStart));

    [PatchPostfix]
    public static void Postfix(MongoId __2)
    {
        // (url, request, sessionID)
        try
        {
            var session = SessionManager.Instance?.Get(__2.ToString());
            if (session is null)
            {
                return;
            }

            lock (session)
            {
                session.OnGameStarted();
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"game start: {ex}");
        }
    }
}

/// <summary>Holds a session's lock across a patched method (prefix to finalizer, same thread).</summary>
internal static class SessionLock
{
    public static ApSession? Enter(MongoId sessionId, Type patchType)
    {
        if (!PatchInstaller.IsReady(patchType))
        {
            // Without its finalizer attached the lock would never be released.
            return null;
        }

        try
        {
            // Taken whether or not anything is pending: an item may arrive while the response is built.
            var session = SessionManager.Instance?.Get(sessionId.ToString());
            if (session is null)
            {
                return null;
            }

            Monitor.Enter(session);
            return session;
        }
        catch
        {
            return null;
        }
    }

    public static void Exit(ApSession? session, Action<ApSession>? action)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            action?.Invoke(session);
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"profile sync: {ex}");
        }
        finally
        {
            Monitor.Exit(session);
        }
    }
}

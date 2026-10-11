using System.Reflection;
using ArchipelagoTarkov.Ap;
using ArchipelagoTarkov.Data;
using ArchipelagoTarkov.Game;
using ArchipelagoTarkov.Patches;
using ArchipelagoTarkov.State;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace ArchipelagoTarkov.Session;

/// <summary>Owns every profile's <see cref="ApSession"/>, drives them from the SPT update loop and installs the patches.</summary>
[Injectable(InjectionType.Singleton, typePriority: OnLoadOrder.PostSptModLoader + 1)]
public class SessionManager(ISptLogger<SessionManager> logger, GameBridge game, ConfigServer configServer) : IOnLoad, IOnUpdate
{
    public static SessionManager? Instance { get; private set; }

    private readonly Dictionary<string, ApSession> _sessions = new();
    private readonly object _lock = new();
    private TarkovData? _data;
    private StateStore? _store;
    private string _modDir = "";
    private bool _loaded;

    public TarkovData Data => _data ?? throw new InvalidOperationException("Archipelago data not loaded");

    public Task OnLoad()
    {
        try
        {
            _modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
            _data = TarkovData.LoadEmbedded();
            _store = new StateStore(_modDir);

            EnableChatBot();
            PatchInstaller.InstallAll(Log, ResolveRuntimeTypes(PatchInstaller.PatchedTypes));

            lock (_lock)
            {
                foreach (var state in _store.LoadAll())
                {
                    _sessions[state.ProfileId] = CreateSession(state);
                }
            }

            Instance = this;
            _loaded = true;
            // Save everything when the server closes (Ctrl+C or the window being closed).
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            logger.Success($"Archipelago for Tarkov loaded ({_data.Quests.Count(q => q.Check)} quest checks, " +
                           $"{_sessions.Count} linked profile{(_sessions.Count == 1 ? "" : "s")}). " +
                           "Message the Archipelago contact in game to connect.");
        }
        catch (Exception ex)
        {
            logger.Error($"Archipelago for Tarkov failed to load: {ex}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The classes the DI container really hands out for these SPT types: other mods may register a
    /// subclass with [Injectable(typeOverride: ...)]. Worked out from the attributes the same way SPT's
    /// DependencyInjectionHandler registers them, without creating any service.
    /// </summary>
    private Dictionary<Type, Type> ResolveRuntimeTypes(IEnumerable<Type> baseTypes)
    {
        var map = new Dictionary<Type, Type>();
        try
        {
            var injectables = new List<(Type Type, Injectable Attribute)>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || !UsesSptDi(assembly))
                {
                    continue;
                }

                Type?[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }

                foreach (var type in types)
                {
                    if (type is { IsClass: true, IsAbstract: false } && type.GetCustomAttribute<Injectable>(false) is { } attribute)
                    {
                        injectables.Add((type, attribute));
                    }
                }
            }

            var replaced = injectables.Where(i => i.Attribute.TypeOverride != null)
                .Select(i => i.Attribute.TypeOverride!.FullName)
                .ToHashSet();
            // SPT registers in TypePriority order (stable) and the last registration of a type wins.
            var registered = injectables.Where(i => !replaced.Contains(i.Type.FullName))
                .OrderBy(i => i.Attribute.TypePriority)
                .ToList();
            foreach (var baseType in baseTypes)
            {
                var winner = registered.LastOrDefault(i => baseType.IsAssignableFrom(i.Type)).Type;
                if (winner != null && winner != baseType)
                {
                    map[baseType] = winner;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[Archipelago] could not check for replaced SPT classes: {ex.Message}");
        }

        return map;
    }

    private static bool UsesSptDi(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        return name is "SPTarkov.Server.Core" or "SPTarkov.DI"
               || assembly.GetReferencedAssemblies().Any(r => r.Name == "SPTarkov.DI");
    }

    private void EnableChatBot()
    {
        var core = configServer.GetConfig<CoreConfig>();
        core.Features.ChatbotFeatures.EnabledBots[GameBridge.ChatBotId] = true;
    }

    // Each profile gets its own name book: two profiles may be linked to different multiworlds.
    private ApSession CreateSession(ProfileState state) =>
        new(state, Data, game, _store!, new NameBook(Path.Combine(_modDir, "cache")), Log);

    private void Log(string message) => logger.Info(message);

    public void LogError(string message) => logger.Error($"[Archipelago] {message}");

    public Task<bool> OnUpdate(long secondsSinceLastRun)
    {
        if (!_loaded)
        {
            return Task.FromResult(true);
        }

        List<ApSession> sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToList();
        }

        foreach (var session in sessions)
        {
            lock (session)
            {
                try
                {
                    session.Update();
                }
                catch (Exception ex)
                {
                    logger.Error($"[Archipelago] update failed for {session.ProfileId}: {ex}");
                }
            }
        }

        return Task.FromResult(true);
    }

    // ------------------------------------------------------------------ lookups used by patches / chat / router
    public ApSession? Get(string profileId)
    {
        lock (_lock)
        {
            return _sessions.GetValueOrDefault(profileId);
        }
    }

    public ApSession GetOrCreate(string profileId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(profileId, out var session))
            {
                session = CreateSession(new ProfileState { ProfileId = profileId });
                _sessions[profileId] = session;
            }

            return session;
        }
    }

    public void Remove(string profileId)
    {
        lock (_lock)
        {
            if (_sessions.Remove(profileId, out var session))
            {
                lock (session)
                {
                    session.Shutdown();
                }
            }

            _store?.Delete(profileId);
        }
    }

    public string? MapKeyFor(string? locationId)
    {
        if (string.IsNullOrEmpty(locationId) || _data is null)
        {
            return null;
        }

        return _data.MapKeyByLocationId.GetValueOrDefault(locationId);
    }

    /// <summary>Map keys locked for this profile, or null when the profile is not in an Archipelago game.</summary>
    public HashSet<string>? LockedMaps(string profileId)
    {
        var session = Get(profileId);
        if (session is null || _data is null)
        {
            return null;
        }

        lock (session)
        {
            if (!session.Active)
            {
                return null;
            }

            return _data.Maps.Where(m => !session.IsMapUnlocked(m.Key)).Select(m => m.Key).ToHashSet();
        }
    }

    public bool BlocksTraderUnlock(string profileId, string traderId)
    {
        var session = Get(profileId);
        if (session is null || _data is null || !_data.TradersById.TryGetValue(traderId, out var trader))
        {
            return false;
        }

        lock (session)
        {
            return session.Active && !session.IsTraderUnlocked(trader.Key);
        }
    }

    private int _shutDown;

    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutDown, 1) == 1)
        {
            return;
        }

        List<ApSession> sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToList();
        }

        foreach (var session in sessions)
        {
            // Never hang the server's exit on a busy session.
            if (!Monitor.TryEnter(session, TimeSpan.FromSeconds(1)))
            {
                continue;
            }

            try
            {
                session.Shutdown();
            }
            catch (Exception ex)
            {
                logger.Error($"[Archipelago] shutdown failed for {session.ProfileId}: {ex.Message}");
            }
            finally
            {
                Monitor.Exit(session);
            }
        }
    }
}

using System.Reflection;
using ArchipelagoTarkov.Ap;
using ArchipelagoTarkov.Data;
using ArchipelagoTarkov.Game;
using ArchipelagoTarkov.Patches;
using ArchipelagoTarkov.State;
using SPTarkov.DI;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;

namespace ArchipelagoTarkov.Session;

/// <summary>Owns every profile's <see cref="ApSession"/>, drives them from the SPT update loop and installs the patches.</summary>
[Injectable(InjectionType.Singleton, OnLoadOrder.PostLoad + 1)]
public class SessionManager(
    ISptLogger<SessionManager> logger,
    GameBridge game,
    CoreConfig coreConfig,
    IReadOnlyList<DependencyInjectionContainer> registrations
) : IOnLoad, IOnUpdate
{
    public static SessionManager? Instance { get; private set; }

    private readonly Dictionary<string, ApSession> _sessions = new();
    private readonly object _lock = new();
    private TarkovData? _data;
    private StateStore? _store;
    private string _modDir = "";
    private bool _loaded;

    public TarkovData Data => _data ?? throw new InvalidOperationException("Archipelago data not loaded");

    public Task OnLoadAsync(CancellationToken cancellationToken)
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
    /// The classes the DI container really hands out for these SPT types. Another mod can replace an SPT class by
    /// registering a subclass of it; SPT registers types in TypePriority order and the last registration of a
    /// type wins. SPT publishes that registration list, so nothing has to be created to find out.
    /// </summary>
    private Dictionary<Type, Type> ResolveRuntimeTypes(IEnumerable<Type> baseTypes)
    {
        var map = new Dictionary<Type, Type>();
        try
        {
            foreach (var baseType in baseTypes)
            {
                var winner = registrations.LastOrDefault(r => r.Type == baseType)?.ParentType;
                if (winner != null && winner != baseType && baseType.IsAssignableFrom(winner))
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

    private void EnableChatBot()
    {
        coreConfig.Features.ChatbotFeatures.EnabledBots[GameBridge.ChatBotId] = true;
    }

    // Each profile gets its own name book: two profiles may be linked to different multiworlds.
    private ApSession CreateSession(ProfileState state) =>
        new(state, Data, game, _store!, new NameBook(Path.Combine(_modDir, "cache")), Log);

    private void Log(string message) => logger.Info(message);

    public void LogError(string message) => logger.Error($"[Archipelago] {message}");

    public Task<bool> OnUpdateAsync(long secondsSinceLastRun, CancellationToken cancellationToken)
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

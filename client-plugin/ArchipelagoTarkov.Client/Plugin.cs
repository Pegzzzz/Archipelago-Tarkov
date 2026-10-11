using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using SPT.Common.Http;
using UnityEngine;

namespace ArchipelagoTarkov.Client
{
    /// <summary>
    /// Optional client side of Archipelago for Tarkov.
    ///  * shows Archipelago messages as in-game notifications (also during raids)
    ///  * carries out DeathLink deaths received while you are in a raid
    /// It talks only to the SPT server mod (GET /archipelago/client/poll). Game internals are reached
    /// through reflection, so if a game update renames something the plugin logs a warning and keeps
    /// running instead of breaking the game.
    /// </summary>
    [BepInPlugin(Guid, "Archipelago for Tarkov (client)", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.pegzzzz.archipelago-tarkov.client";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;

        private ConfigEntry<bool> _notifications;
        private ConfigEntry<bool> _deathLink;
        private ConfigEntry<float> _inRaidInterval;
        private ConfigEntry<float> _menuInterval;

        private Task<string> _pending;
        private float _nextPoll;
        private bool _serverMissing;
        private int _failures;

        private void Awake()
        {
            Log = Logger;
            GameAccess.LogInfo = text => Logger.LogInfo(text);
            GameAccess.LogWarning = text => Logger.LogWarning(text);
            _notifications = Config.Bind("General", "Show notifications", true,
                "Show Archipelago messages (items received, checks sent, chat) as in-game notifications.");
            _deathLink = Config.Bind("General", "Allow DeathLink deaths", true,
                "When DeathLink is on in your Archipelago game, die in raid when another player dies.");
            _inRaidInterval = Config.Bind("Advanced", "Poll interval in raid (seconds)", 2f,
                new ConfigDescription("How often to ask the SPT server for news during a raid.", new AcceptableValueRange<float>(1f, 30f)));
            _menuInterval = Config.Bind("Advanced", "Poll interval in menus (seconds)", 5f,
                new ConfigDescription("How often to ask the SPT server for news outside raids.", new AcceptableValueRange<float>(2f, 60f)));
            Logger.LogInfo($"Archipelago for Tarkov client {Version} loaded");
        }

        private void Update()
        {
            if (_pending != null)
            {
                if (!_pending.IsCompleted)
                {
                    return;
                }

                var task = _pending;
                _pending = null;
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    _failures = 0;
                    Handle(task.Result);
                }
                else
                {
                    _failures++;
                    if (_failures == 3)
                    {
                        Logger.LogWarning("Archipelago: cannot reach the SPT server mod (is ArchipelagoTarkov installed on the server?). Will keep trying quietly.");
                    }
                }
            }

            if (_serverMissing || Time.realtimeSinceStartup < _nextPoll)
            {
                return;
            }

            var inRaid = GameAccess.InRaid();
            var interval = inRaid ? _inRaidInterval.Value : _menuInterval.Value;
            if (_failures > 3)
            {
                interval = Math.Max(interval, 30f);
            }

            _nextPoll = Time.realtimeSinceStartup + interval;
            _pending = Task.Run(() => RequestHandler.GetJson("/archipelago/client/poll"));
        }

        private void Handle(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            PollResponse response;
            try
            {
                response = JsonUtility.FromJson<PollResponse>(json);
            }
            catch (Exception ex)
            {
                // A 404 page or an older server mod: stop polling instead of spamming errors.
                Logger.LogWarning($"Archipelago: unexpected answer from the SPT server, disabling polling ({ex.Message})");
                _serverMissing = true;
                return;
            }

            if (response == null)
            {
                return;
            }

            if (_notifications.Value && response.messages != null)
            {
                foreach (var message in response.messages.Where(m => !string.IsNullOrEmpty(m)))
                {
                    GameAccess.Notify(message);
                }
            }

            if (response.kill && _deathLink.Value && GameAccess.InRaid())
            {
                if (GameAccess.KillMainPlayer())
                {
                    GameAccess.Notify(string.IsNullOrEmpty(response.cause) ? "DeathLink!" : $"DeathLink: {response.cause}");
                    Task.Run(() => RequestHandler.GetJson("/archipelago/client/killed"));
                }
            }
        }
    }

    [Serializable]
    public class PollResponse
    {
        public bool linked;
        public bool connected;
        public string[] messages;
        public bool kill;
        public string cause;
    }

    /// <summary>Reflection access to the game, resolved once and cached.</summary>
    internal static class GameAccess
    {
        // Set by the plugin; kept separate so this class never depends on BepInEx/Unity types.
        public static Action<string> LogInfo = _ => { };
        public static Action<string> LogWarning = _ => { };

        private static bool _resolved;
        private static PropertyInfo _gameWorldInstantiated;
        private static PropertyInfo _gameWorldInstance;
        private static FieldInfo _mainPlayerField;
        private static PropertyInfo _mainPlayerProperty;
        private static PropertyInfo _activeHealthController;
        private static MethodInfo _kill;
        private static MethodInfo _killMe;
        private static MethodInfo _notify;
        private static readonly HashSet<string> Warned = new HashSet<string>();

        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;
            try
            {
                ResolveMembers();
            }
            catch (Exception ex)
            {
                // Never let a lookup problem escape into Unity's Update loop.
                Warn($"could not inspect the game ({ex.GetBaseException().Message}); notifications and DeathLink may not work");
            }
        }

        private static IEnumerable<MethodInfo> SafeMethods(Type type, BindingFlags flags, Func<MethodInfo, bool> predicate)
        {
            if (type == null)
            {
                return Enumerable.Empty<MethodInfo>();
            }

            return type.GetMethods(flags).Where(m =>
            {
                try
                {
                    return predicate(m);
                }
                catch
                {
                    return false; // a method whose signature references a missing assembly
                }
            });
        }

        private static void ResolveMembers()
        {
            var game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            var comfort = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Comfort");
            if (game == null || comfort == null)
            {
                Warn("game assemblies not found");
                return;
            }

            var gameWorld = game.GetType("EFT.GameWorld");
            var singleton = comfort.GetType("Comfort.Common.Singleton`1");
            if (gameWorld != null && singleton != null)
            {
                var closed = singleton.MakeGenericType(gameWorld);
                _gameWorldInstantiated = closed.GetProperty("Instantiated", BindingFlags.Public | BindingFlags.Static);
                _gameWorldInstance = closed.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _mainPlayerField = gameWorld.GetField("MainPlayer", BindingFlags.Public | BindingFlags.Instance);
                _mainPlayerProperty = gameWorld.GetProperty("MainPlayer", BindingFlags.Public | BindingFlags.Instance);
            }

            var player = game.GetType("EFT.Player");
            _activeHealthController = player?.GetProperty("ActiveHealthController", BindingFlags.Public | BindingFlags.Instance);
            var health = _activeHealthController?.PropertyType;
            _kill = SafeMethods(health, BindingFlags.Public | BindingFlags.Instance,
                    m => m.Name == "Kill" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum)
                .FirstOrDefault();

            // Fallback: Player.KillMe(EBodyPartColliderType, float)
            _killMe = SafeMethods(player, BindingFlags.Public | BindingFlags.Instance,
                    m => m.Name == "KillMe" && m.GetParameters().Length == 2
                         && m.GetParameters()[0].ParameterType.IsEnum && m.GetParameters()[1].ParameterType == typeof(float))
                .FirstOrDefault();

            var notifications = game.GetType("NotificationManagerClass");
            _notify = SafeMethods(notifications, BindingFlags.Public | BindingFlags.Static,
                    m => m.Name == "DisplayMessageNotification" && m.GetParameters().Length >= 1
                         && m.GetParameters()[0].ParameterType == typeof(string))
                .FirstOrDefault();

            if (_gameWorldInstantiated == null) Warn("could not find the game world (raid detection disabled)");
            if (_kill == null && _killMe == null) Warn("could not find a way to kill the player (DeathLink deaths disabled)");
            if (_notify == null) Warn("could not find the notification manager (notifications go to the BepInEx log instead)");
        }

        private static void Warn(string text)
        {
            if (Warned.Add(text))
            {
                LogWarning($"Archipelago: {text}");
            }
        }

        public static bool InRaid()
        {
            Resolve();
            try
            {
                return _gameWorldInstantiated != null && (bool)_gameWorldInstantiated.GetValue(null, null) && MainPlayer() != null;
            }
            catch
            {
                return false;
            }
        }

        private static object MainPlayer()
        {
            var world = _gameWorldInstance?.GetValue(null, null);
            if (world == null)
            {
                return null;
            }

            return _mainPlayerField != null ? _mainPlayerField.GetValue(world) : _mainPlayerProperty?.GetValue(world, null);
        }

        public static bool KillMainPlayer()
        {
            Resolve();
            var player = MainPlayer();
            if (player == null)
            {
                return false;
            }

            try
            {
                var health = _activeHealthController?.GetValue(player, null);
                if (health != null && _kill != null)
                {
                    var damageType = _kill.GetParameters()[0].ParameterType;
                    var cause = Enum.IsDefined(damageType, "Undefined") ? Enum.Parse(damageType, "Undefined") : Enum.ToObject(damageType, 0);
                    _kill.Invoke(health, new[] { cause });
                    return true;
                }
            }
            catch (Exception ex)
            {
                Warn($"DeathLink kill failed: {ex.GetBaseException().Message}");
            }

            try
            {
                if (_killMe != null)
                {
                    var collider = _killMe.GetParameters()[0].ParameterType;
                    _killMe.Invoke(player, new[] { Enum.ToObject(collider, 0), (object)9999f });
                    return true;
                }
            }
            catch (Exception ex)
            {
                Warn($"DeathLink fallback kill failed: {ex.GetBaseException().Message}");
            }

            return false;
        }

        public static void Notify(string text)
        {
            Resolve();
            LogInfo($"[Archipelago] {text}");
            if (_notify == null)
            {
                return;
            }

            try
            {
                var parameters = _notify.GetParameters();
                var args = new object[parameters.Length];
                args[0] = text;
                for (var i = 1; i < parameters.Length; i++)
                {
                    var type = parameters[i].ParameterType;
                    if (type.IsEnum)
                    {
                        // duration: Long (1); icon: Default (0)
                        args[i] = Enum.ToObject(type, type.Name.Contains("Duration") ? 1 : 0);
                    }
                    else
                    {
                        args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                    }
                }

                _notify.Invoke(null, args);
            }
            catch (Exception ex)
            {
                Warn($"notification failed: {ex.GetBaseException().Message}");
            }
        }
    }
}

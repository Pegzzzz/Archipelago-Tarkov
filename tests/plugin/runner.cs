using System;
using System.Reflection;
public static class Runner
{
    static int failures = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) failures++; }
    public static int Main(string[] args)
    {
        string mode = args[0];
        var plugin = Assembly.LoadFrom("ArchipelagoTarkov.Client.dll");
        var game = Assembly.LoadFrom("Assembly-CSharp.dll");
        var comfort = Assembly.LoadFrom("Comfort.dll");
        var access = plugin.GetType("ArchipelagoTarkov.Client.GameAccess", true);
        Func<string, object[], object> call = (n, a) => access.GetMethod(n, BindingFlags.Public | BindingFlags.Static).Invoke(null, a);

        Check((bool)call("InRaid", new object[0]) == false, mode + ": not in raid without a game world");
        Check((bool)call("KillMainPlayer", new object[0]) == false, mode + ": kill does nothing outside raid");

        var gw = Activator.CreateInstance(game.GetType("EFT.GameWorld"));
        var player = Activator.CreateInstance(game.GetType("EFT.Player"));
        var health = Activator.CreateInstance(game.GetType("EFT.HealthSystem.ActiveHealthController"));
        game.GetType("EFT.Player").GetProperty("ActiveHealthController").SetValue(player, health, null);
        game.GetType("EFT.GameWorld").GetField("MainPlayer").SetValue(gw, player);
        var singleton = comfort.GetType("Comfort.Common.Singleton`1").MakeGenericType(game.GetType("EFT.GameWorld"));
        singleton.GetProperty("Instance").SetValue(null, gw, null);

        Check((bool)call("InRaid", new object[0]), mode + ": in raid once the game world has a main player");
        var killed = (bool)call("KillMainPlayer", new object[0]);
        var dead = (bool)health.GetType().GetField("Dead").GetValue(health);
        var killMe = (bool)player.GetType().GetField("KilledByKillMe").GetValue(player);
        if (mode == "kill")
        {
            Check(killed && dead, "kill: Kill(EDamageType) was used");
            Check(health.GetType().GetField("Cause").GetValue(health).ToString() == "Undefined", "kill: cause Undefined");
        }
        else if (mode == "killme")
        {
            Check(killed && killMe && !dead, "killme: fell back to Player.KillMe");
        }
        else
        {
            Check(!killed, "none: reports failure when no kill method exists");
        }

        call("Notify", new object[] { "Received Customs Access" });
        var shown = (System.Collections.IList)game.GetType("NotificationManagerClass").GetField("Shown").GetValue(null);
        var durations = (System.Collections.IList)game.GetType("NotificationManagerClass").GetField("Durations").GetValue(null);
        Check(shown.Count == 1 && (string)shown[0] == "Received Customs Access", mode + ": notification shown");
        Check(durations.Count == 1 && (int)durations[0] == 1, mode + ": long duration used");

        singleton.GetProperty("Instance").SetValue(null, null, null);
        Check((bool)call("InRaid", new object[0]) == false, mode + ": raid over when the game world goes away");
        return failures;
    }
}

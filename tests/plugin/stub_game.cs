using System.Collections.Generic;
namespace EFT
{
    public enum EDamageType { Undefined = 0, Bullet = 1 }
    public enum EBodyPartColliderType { None = 0, Head = 1 }
    public class GameWorld { public Player MainPlayer; }
    public class Player
    {
        public EFT.HealthSystem.ActiveHealthController ActiveHealthController { get; set; }
        public bool KilledByKillMe;
#if WITH_KILLME
        public void KillMe(EBodyPartColliderType collider, float damage) { KilledByKillMe = true; }
#endif
    }
}
namespace EFT.HealthSystem
{
    public class ActiveHealthController
    {
        public bool Dead;
        public EFT.EDamageType Cause = (EFT.EDamageType)(-1);
#if WITH_KILL
        public void Kill(EFT.EDamageType damageType) { Dead = true; Cause = damageType; }
#endif
    }
}
namespace EFT.Communications
{
    public enum ENotificationDurationType { Default = 0, Long = 1, Infinite = 2 }
    public enum ENotificationIconType { Default = 0, Alert = 1 }
}
public class NotificationManagerClass
{
    public static List<string> Shown = new List<string>();
    public static List<int> Durations = new List<int>();
    public static void DisplayMessageNotification(string message,
        EFT.Communications.ENotificationDurationType duration = EFT.Communications.ENotificationDurationType.Default,
        EFT.Communications.ENotificationIconType icon = EFT.Communications.ENotificationIconType.Default,
        UnityEngine.Color? color = null)
    { Shown.Add(message); Durations.Add((int)duration); }
}

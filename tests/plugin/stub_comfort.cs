namespace Comfort.Common
{
    public class Singleton<T> where T : class
    {
        public static T Instance { get; set; }
        public static bool Instantiated { get { return Instance != null; } }
    }
}

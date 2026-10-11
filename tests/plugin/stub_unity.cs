// Minimal stand-in for UnityEngine.CoreModule so the plugin's logic can run outside the game.
namespace UnityEngine
{
    public struct Color { public float r, g, b, a; }
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
}

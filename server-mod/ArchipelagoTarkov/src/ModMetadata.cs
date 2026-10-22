using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace ArchipelagoTarkov;

public sealed class ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.pegzzzz.archipelago-tarkov";
    public string Name { get; init; } = "Archipelago for Tarkov";
    public string Author { get; init; } = "Pegzzzz";
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new("2.0.0");
    public Range SptVersion { get; init; } = new("~4.1.6");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/Pegzzzz/Archipelago-Tarkov";
    public string License { get; init; } = "MIT";
}

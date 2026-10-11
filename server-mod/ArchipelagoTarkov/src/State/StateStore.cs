using System.Text.Json;

namespace ArchipelagoTarkov.State;

/// <summary>Reads and writes per-profile state files. Writes go to a temp file first so a crash never leaves half a file.</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _directory;
    private readonly object _lock = new();

    public StateStore(string modDirectory)
    {
        _directory = Path.Combine(modDirectory, "profiles");
        Directory.CreateDirectory(_directory);
    }

    public string Directory_ => _directory;

    public IEnumerable<ProfileState> LoadAll()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            ProfileState? state = null;
            try
            {
                state = JsonSerializer.Deserialize<ProfileState>(File.ReadAllText(file), Options);
            }
            catch (Exception)
            {
                // A broken file must not stop the server; keep a copy for the user and skip it.
                try
                {
                    File.Copy(file, file + ".broken", true);
                }
                catch
                {
                    // ignored
                }
            }

            if (state != null && !string.IsNullOrEmpty(state.ProfileId))
            {
                yield return state;
            }
        }
    }

    public void Save(ProfileState state)
    {
        lock (_lock)
        {
            var path = Path.Combine(_directory, $"{state.ProfileId}.json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
            File.Move(tmp, path, true);
        }
    }

    public void Delete(string profileId)
    {
        lock (_lock)
        {
            var path = Path.Combine(_directory, $"{profileId}.json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

using WasabiDrive.Core.Models;

namespace WasabiDrive.Core;

/// <summary>Loads and saves the list of bucket→drive mappings (mappings.json). No secrets here.</summary>
public sealed class MappingStore
{
    private readonly string _filePath;

    public MappingStore(string? filePath = null) => _filePath = filePath ?? AppPaths.MappingsFile;

    public List<Mapping> Load() =>
        JsonFileStore.Read(_filePath, () => new List<Mapping>());

    public void Save(IEnumerable<Mapping> mappings) =>
        JsonFileStore.Write(_filePath, mappings.ToList());
}

/// <summary>Loads and saves app-wide settings (settings.json).</summary>
public sealed class SettingsStore
{
    private readonly string _filePath;

    public SettingsStore(string? filePath = null) => _filePath = filePath ?? AppPaths.SettingsFile;

    public AppSettings Load() =>
        JsonFileStore.Read(_filePath, () => new AppSettings());

    public void Save(AppSettings settings) =>
        JsonFileStore.Write(_filePath, settings);
}

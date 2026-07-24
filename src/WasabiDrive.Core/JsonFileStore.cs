using System.Text.Json;
using System.Text.Json.Serialization;

namespace WasabiDrive.Core;

/// <summary>Shared JSON options + atomic read/write helpers for the plaintext config files.</summary>
internal static class JsonFileStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Read<T>(string path, Func<T> createDefault)
    {
        if (!File.Exists(path))
            return createDefault();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, Options) ?? createDefault();
    }

    public static void Write<T>(string path, T value)
    {
        AppPaths.EnsureCreated();
        var json = JsonSerializer.Serialize(value, Options);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}

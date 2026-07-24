using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Serialization;
using WasabiDrive.Core.Models;

namespace WasabiDrive.Core;

/// <summary>
/// A portable snapshot of app configuration — settings plus bucket→drive mappings — with no
/// secret material. Wasabi keys are intentionally excluded: they are DPAPI-encrypted and bound to
/// the current Windows user+machine, so they cannot be meaningfully moved. Re-enter keys after import.
/// </summary>
public sealed class SettingsBundle
{
    public string SchemaVersion { get; set; } = "1";
    public string ExportedByVersion { get; set; } = AppInfo.CurrentVersionString;
    public AppSettings Settings { get; set; } = new();

    // XmlSerializer needs a concrete, settable collection type.
    public List<Mapping> Mappings { get; set; } = new();
}

/// <summary>Reads/writes <see cref="SettingsBundle"/> as JSON or XML, chosen by file extension.</summary>
public static class SettingsPortability
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>True if the path's extension is one we can import/export (.json or .xml).</summary>
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".json" or ".xml";
    }

    public static void Export(string path, AppSettings settings, IEnumerable<Mapping> mappings)
    {
        var bundle = new SettingsBundle
        {
            Settings = settings,
            Mappings = mappings.ToList(),
        };

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".xml")
        {
            using var writer = XmlWriter.Create(path, new XmlWriterSettings { Indent = true });
            new XmlSerializer(typeof(SettingsBundle)).Serialize(writer, bundle);
        }
        else
        {
            File.WriteAllText(path, JsonSerializer.Serialize(bundle, JsonOptions));
        }
    }

    public static SettingsBundle Import(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".xml")
        {
            using var reader = XmlReader.Create(path);
            return (SettingsBundle?)new XmlSerializer(typeof(SettingsBundle)).Deserialize(reader)
                ?? throw new InvalidDataException("The XML file did not contain a settings bundle.");
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SettingsBundle>(json, JsonOptions)
            ?? throw new InvalidDataException("The JSON file did not contain a settings bundle.");
    }
}

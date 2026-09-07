using WasabiDrive.Core;
using WasabiDrive.Core.Models;
using WasabiDrive.WinFsp;

// A throwaway harness for the rclone-free drive-letter engine. It reuses the real
// mappings.json + DPAPI credential store, so it mounts exactly what the tray app would —
// minus rclone.exe. Drive letter is taken from the mapping unless overridden.
//
//   WasabiDrive.WinFsp.Probe                 list mappings
//   WasabiDrive.WinFsp.Probe <name> [Z]      mount that mapping, Ctrl+C to unmount

if (!S3MountManager.TryGetDriverVersion(out var driver, out var driverError))
{
    Console.Error.WriteLine(driverError);
    return 2;
}
Console.WriteLine($"WinFsp driver {driver} (binding requires {S3MountManager.RequiredDriverVersion}.x)");

var mappings = new MappingStore().Load();
if (mappings.Count == 0)
{
    Console.Error.WriteLine($"No mappings in {AppPaths.MappingsFile}");
    return 2;
}

if (args.Length == 0)
{
    Console.WriteLine($"\nMappings in {AppPaths.MappingsFile}:\n");
    foreach (var m in mappings)
    {
        Console.WriteLine($"  {m.Name,-16} {m.BucketName,-20} {m.RegionCode,-14} " +
                          $"{m.DriveTarget}  mode={m.Mode}  dir-cache={m.Cache.DirCacheTime}");
    }
    Console.WriteLine("\nUsage: WasabiDrive.WinFsp.Probe <mapping name> [drive letter]");
    return 0;
}

var mapping = mappings.FirstOrDefault(m =>
    string.Equals(m.Name, args[0], StringComparison.OrdinalIgnoreCase));
if (mapping is null)
{
    Console.Error.WriteLine($"No mapping named '{args[0]}'.");
    return 2;
}

if (args.Length > 1) mapping.DriveLetter = args[1].TrimEnd(':');

var credentialStore = new CredentialStore();
credentialStore.Load();
var credentials = credentialStore.Get(mapping.Id);
if (credentials is null)
{
    Console.Error.WriteLine($"No saved credentials for '{mapping.Name}'. Add it in the tray app first.");
    return 2;
}

using var manager = new S3MountManager(line => Console.WriteLine($"  {line}"));

try
{
    manager.Mount(mapping, credentials);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Mount failed: {ex.Message}");
    return 1;
}

Console.WriteLine($"\n{mapping.BucketName} is mounted on {mapping.DriveTarget} — no rclone process.");
Console.WriteLine("Press Ctrl+C to unmount.\n");

using var stop = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
stop.Wait();

Console.WriteLine("Unmounting...");
manager.Unmount(mapping.Id);
return 0;

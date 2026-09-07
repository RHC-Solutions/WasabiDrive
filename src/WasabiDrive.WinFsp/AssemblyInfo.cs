using System.Runtime.CompilerServices;

// The splice planner and dirty-range set decide which bytes of an object survive an edit, so they
// are unit-tested directly rather than only through a live mount.
[assembly: InternalsVisibleTo("WasabiDrive.WinFsp.Tests")]

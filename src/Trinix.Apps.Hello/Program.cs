using System.Globalization;
using System.Runtime.InteropServices;

// Hello — the reference Trinix application.
//
// Everything it prints is something the launcher, the bundle format or the
// runtime had to get right for it to be printed at all, so the output doubles
// as the assertion the VM gate reads:
//
//   * TRINIX_BUNDLE     the launcher passed the bundle location through execve,
//                       which is the only way this process can know it
//   * the resource file  the bundle's Resources directory survived being sealed,
//                       packed into erofs, mounted, copied to /Applications and
//                       verified — a byte-exact round trip of the whole chain
//   * the runtime version  a framework-dependent apphost found the shared
//                       runtime from inside /Applications

Console.WriteLine("TRINIX-HELLO: starting");

var bundle = Environment.GetEnvironmentVariable("TRINIX_BUNDLE");
var identifier = Environment.GetEnvironmentVariable("TRINIX_BUNDLE_IDENTIFIER");
var resources = Environment.GetEnvironmentVariable("TRINIX_BUNDLE_RESOURCES");

Console.WriteLine($"TRINIX-HELLO: bundle={bundle ?? "(not launched by trinix-open)"}");
Console.WriteLine($"TRINIX-HELLO: identifier={identifier ?? "(unknown)"}");
Console.WriteLine($"TRINIX-HELLO: runtime={Environment.Version} on {RuntimeInformation.OSArchitecture}");

if (args.Length > 0) {
    Console.WriteLine($"TRINIX-HELLO: arguments={string.Join(' ', args)}");
}

// The resource is read through the environment rather than relative to the
// executable, because that is what the contract says an application should do —
// and because AppContext.BaseDirectory would happen to work here and would stop
// working for anything launched through a symlink.
var greetingPath = resources is null
    ? Path.Combine(AppContext.BaseDirectory, "..", "Resources", "greeting.txt")
    : Path.Combine(resources, "greeting.txt");

if (File.Exists(greetingPath)) {
    Console.WriteLine($"TRINIX-HELLO: greeting={File.ReadAllText(greetingPath).Trim()}");
} else {
    Console.WriteLine($"TRINIX-HELLO: greeting is missing at {greetingPath}");
    return 1;
}

Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TRINIX-HELLO-OK {DateTimeOffset.UtcNow:u}"));
return 0;

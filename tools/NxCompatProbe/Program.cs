using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

// NxCompatProbe: probes a Siemens NX installation and the local .NET runtimes,
// and prints a compatibility profile as JSON. Read-only. Exit code 0 = probed
// OK (not "compatible" — callers must judge the profile values).

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: NxCompatProbe <NX root directory> [--json <output path>]");
    return 2;
}

var nxRoot = Path.GetFullPath(args[0]);
if (!Directory.Exists(nxRoot))
{
    Console.Error.WriteLine($"NX root not found: {nxRoot}");
    return 2;
}

var probe = new Dictionary<string, object?>
{
    ["probeTimeUtc"] = DateTimeOffset.UtcNow,
    ["nxRoot"] = nxRoot,
};

// --- NX install identity ---------------------------------------------------
probe["ugiiBaseDirExists"] = Directory.Exists(Path.Combine(nxRoot, "UGII"));
var envFile = Path.Combine(nxRoot, "UGII", "ugii_env_ug.dat");
if (File.Exists(envFile))
{
    var managedMajor = File.ReadAllLines(envFile)
        .Select(l => l.Trim())
        .FirstOrDefault(l => l.StartsWith("UGII_NXOPEN_DOTNET_CORE_MAJOR_VERSION=", StringComparison.Ordinal))
        ?.Split('=', 2)[1];
    probe["ugiiNxopenDotnetCoreMajorVersion"] = managedMajor;
}
else
{
    probe["ugiiNxopenDotnetCoreMajorVersion"] = null;
}

// --- NXOpen assemblies in both managed load contexts -----------------------
foreach (var (key, dir) in new[]
{
    ("managed", Path.Combine(nxRoot, "NXBIN", "managed")),
    ("managedCore", Path.Combine(nxRoot, "NXBIN", "managed_core")),
})
{
    probe[$"nxOpen.{key}"] = InspectAssembly(Path.Combine(dir, "NXOpen.dll"));
    probe[$"nxOpenUf.{key}"] = InspectAssembly(Path.Combine(dir, "NXOpen.UF.dll"));
}

// --- installed .NET 8 runtimes -----------------------------------------------
var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? @"C:\Program Files\dotnet";
probe["netCoreAppRuntimes"] = ListRuntimes(dotnetRoot, "Microsoft.NETCore.App");
probe["windowsDesktopRuntimes"] = ListRuntimes(dotnetRoot, "Microsoft.WindowsDesktop.App");

var json = JsonSerializer.Serialize(probe, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);

if (args.Length >= 3 && args[1] == "--json")
{
    File.WriteAllText(args[2], json, new UTF8Encoding(false));
}

return 0;

static object? InspectAssembly(string path)
{
    if (!File.Exists(path))
    {
        return null;
    }

    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    var def = md.GetAssemblyDefinition();
    var targetFramework = ReadTargetFramework(md);
    var arch = pe.PEHeaders.CoffHeader.Machine switch
    {
        Machine.Amd64 => "x64",
        Machine.I386 => "msil",
        _ => pe.PEHeaders.CoffHeader.Machine.ToString(),
    };
    return new Dictionary<string, object?>
    {
        ["path"] = path,
        ["assemblyVersion"] = def.Version.ToString(),
        ["targetFramework"] = targetFramework,
        ["architecture"] = arch,
    };
}

static string? ReadTargetFramework(MetadataReader md)
{
    foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
    {
        var attr = md.GetCustomAttribute(handle);
        var ctor = md.GetMemberReference((MemberReferenceHandle)attr.Constructor);
        var type = md.GetTypeReference((TypeReferenceHandle)ctor.Parent);
        if (md.GetString(type.Name) == "TargetFrameworkAttribute")
        {
            var blob = md.GetBlobReader(attr.Value);
            blob.ReadUInt16(); // prolog 0x0001
            return blob.ReadUTF8(blob.ReadCompressedInteger());
        }
    }
    return null;
}

static List<string> ListRuntimes(string dotnetRoot, string framework)
{
    var dir = Path.Combine(dotnetRoot, "shared", framework);
    if (!Directory.Exists(dir))
    {
        return new List<string>();
    }
    return Directory.GetDirectories(dir)
        .Select(Path.GetFileName)
        .Where(n => n is not null)
        .Select(n => n!)
        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

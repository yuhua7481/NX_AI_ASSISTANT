// ============================================================
// NxLoadValidation
//
// Phase 0 read-only validation journal, run via run_journal.exe:
//   & "D:\Program Files\Siemens\DesigncenterNX2512\NXBIN\run_journal.exe" `
//       NxLoadValidation.cs -args "<pluginDir>" "<resultPath>"
//
// Validates inside a real NX2512 process:
//   1. the NXOpen runtime actually hosting this journal (version + location + ALC),
//   2. the deployed plugin loads with NXOpen references resolved into NX's own
//      AssemblyLoadContext (equivalent of what NX ManagedLoader does for
//      application-folder DLLs; plain Assembly.LoadFrom loads a second NXOpen
//      copy and breaks NXOpen type identity),
//   3. NxSessionReader.ReadActivePart() runs against the real session,
//   4. the result is written to resultPath so the caller can assert.
//
// Read-only: opens no part, mutates nothing, saves nothing.
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using NXOpen;

public class NxLoadValidation
{
    private sealed class PluginAlc : AssemblyLoadContext
    {
        private readonly string _pluginDir;
        private readonly AssemblyLoadContext _nxAlc;
        private readonly string _managedCoreDir;
        public Assembly ResolvedNxOpen;

        public PluginAlc(string pluginDir, AssemblyLoadContext nxAlc, string managedCoreDir)
            : base("NXAI-Validation", isCollectible: true)
        {
            // NX's own ALC is collectible; a non-collectible plugin ALC may not
            // reference NX's collectible NXOpen assemblies (0x80131515).
            _pluginDir = pluginDir;
            _nxAlc = nxAlc;
            _managedCoreDir = managedCoreDir;
        }

        protected override Assembly Load(AssemblyName name)
        {
            if (name.Name is not null && name.Name.StartsWith("NXOpen", StringComparison.Ordinal))
            {
                // Reuse the EXACT instances NX already loaded in its own ALC.
                // LoadFromAssemblyPath would add a second copy -> split type universe.
                foreach (var loaded in _nxAlc.Assemblies)
                {
                    if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.Ordinal))
                    {
                        ResolvedNxOpen = loaded;
                        return loaded;
                    }
                }
                return _nxAlc.LoadFromAssemblyPath(Path.Combine(_managedCoreDir, name.Name + ".dll"));
            }

            var candidate = Path.Combine(_pluginDir, name.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }

    public static void Main(string[] args)
    {
        var log = new List<string>();

        if (args.Length < 2)
        {
            log.Add("FAIL: usage: NxLoadValidation.cs -args <pluginDir> <resultPath> [partPath]");
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "nxai_validation_result.txt"), log);
            return;
        }

        var pluginDir = Path.GetFullPath(args[0]);
        var resultPath = Path.GetFullPath(args[1]);
        var partPath = args.Length > 2 && args[2].Length > 0 ? Path.GetFullPath(args[2]) : null;

        try
        {
            var session = Session.GetSession();
            var nxopenAsm = typeof(Session).Assembly;
            log.Add($"NXOpen runtime: {nxopenAsm.GetName().FullName}");
            log.Add($"NXOpen location: {nxopenAsm.Location}");
            log.Add($"CoreCLR runtime version: {System.Environment.Version}");
            var nxAlc = AssemblyLoadContext.GetLoadContext(nxopenAsm) ?? AssemblyLoadContext.Default;
            log.Add($"NXOpen ALC: {nxAlc.Name ?? "(default)"}");
            var managedCoreDir = Path.GetDirectoryName(nxopenAsm.Location)!;

            var hostDll = Path.Combine(pluginDir, "NXAI.Assistant.NxHost.NX2512.dll");
            log.Add($"Loading plugin host: {hostDll}");
            var alc = new PluginAlc(pluginDir, nxAlc, managedCoreDir);
            var asm = alc.LoadFromAssemblyPath(hostDll);
            log.Add($"Loaded: {asm.FullName}");
            log.Add($"From: {asm.Location}");
            log.Add($"Plugin ALC: {AssemblyLoadContext.GetLoadContext(asm)?.Name}");

            var sessionTypeInPlugin = asm.GetType("NXOpen.Session", throwOnError: false);
            var identityBeforeCall = ReferenceEquals(alc.ResolvedNxOpen, nxopenAsm);

            var readerType = asm.GetType("NXAI.Assistant.NxHost.NX2512.NxSessionReader", throwOnError: true);

            if (partPath is not null)
            {
                log.Add($"Opening (read-only, never saved): {partPath}");
                NXOpen.PartLoadStatus loadStatus;
                session.Parts.OpenActiveDisplay(partPath, NXOpen.DisplayPartOption.AllowAdditional, out loadStatus);
                loadStatus.Dispose();
            }
            var reader = Activator.CreateInstance(readerType);
            var readMethod = readerType.GetMethod("ReadActivePart", Type.EmptyTypes)
                ?? throw new MissingMethodException(readerType.FullName, "ReadActivePart");
            var snapshot = readMethod.Invoke(reader, null);

            log.Add($"Snapshot type: {snapshot!.GetType().FullName}");
            foreach (var prop in snapshot.GetType().GetProperties())
            {
                var value = prop.GetValue(snapshot) ?? "(null)";
                log.Add($"  {prop.Name} = {value}");
            }

            log.Add($"NXOpen identity pre-call: {identityBeforeCall}; post-call: {ReferenceEquals(alc.ResolvedNxOpen, nxopenAsm)}");

            var displayPart = session.Parts.Display;
            log.Add($"Session display part is {(displayPart is null ? "null" : displayPart.FullPath)}");

            log.Add("RESULT: OK");
        }
        catch (Exception ex)
        {
            log.Add("RESULT: FAILED");
            log.Add(ex.ToString());
        }

        try
        {
            var lw = Session.GetSession().ListingWindow;
            lw.Open();
            foreach (var line in log)
            {
                lw.WriteLine(line);
            }
        }
        catch
        {
            // listing window is best-effort; the result file is authoritative
        }

        File.WriteAllLines(resultPath, log);
    }
}

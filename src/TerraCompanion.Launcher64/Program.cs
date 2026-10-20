using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;

namespace TerraCompanion.Launcher64;

/// <summary>
/// Runs the converted Terraria.exe (in this folder) on 64-bit .NET 8.
///   Terraria64.exe [game args]   play
///   Terraria64.exe --verify      load the game and JIT-compile every method, report anything .NET 8 / FNA can't bind; no window
/// Crashes are written to crash64.log next to this file.
/// </summary>
static class Program
{
    static readonly string Dir = AppContext.BaseDirectory;

    [STAThread]
    static int Main(string[] args)
    {
        Directory.SetCurrentDirectory(Dir);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // FNA, Steamworks.NET, the TerraCompanion hooks... live next to us
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var p = Path.Combine(Dir, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Unhandled: " + e.ExceptionObject);
        if (Environment.GetEnvironmentVariable("TC64_DEBUG") == "1")
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => Log("first chance: " + e.Exception.GetType().Name + ": " + e.Exception.Message + " @ " + e.Exception.TargetSite);

        try
        {
            var game = Assembly.LoadFrom(Path.Combine(Dir, "Terraria.exe"));
            // The game's own resolver loads an embedded library from bytes on every request, which on .NET 8 gives each requester
            // its own copy (and its own types). Load each one once, into the default context, before the game's resolver is asked.
            var embedded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            var resources = game.GetManifestResourceNames();
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                string name = new AssemblyName(e.Name).Name ?? "";
                lock (embedded)
                {
                    if (embedded.TryGetValue(name, out var done)) return done;
                    var res = resources.FirstOrDefault(r => r.EndsWith("." + name + ".dll", StringComparison.OrdinalIgnoreCase));
                    if (res == null) return null;
                    using var s = game.GetManifestResourceStream(res)!;
                    return embedded[name] = AssemblyLoadContext.Default.LoadFromStream(s);
                }
            };
            var launch = game.GetType("Terraria.WindowsLaunch", true)!.GetMethod("Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            if (args.Length > 0 && args[0] == "--verify") return Verify(game, launch);
            launch.Invoke(null, new object[] { args });
            return 0;
        }
        catch (Exception e)
        {
            Log(e.ToString());
            System.Windows.Forms.MessageBox.Show("Terraria (64-bit) could not start:\n\n" + (e is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : e.Message) +
                "\n\nDetails: " + Path.Combine(Dir, "crash64.log"), "TerraCompanion 64-bit", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>Installs the game's embedded-library resolver, then compiles every method of the game and its embedded libraries.</summary>
    static int Verify(Assembly game, MethodInfo launch)
    {
        // (the embedded-library resolver is already registered by Main)
        var unloadable = new SortedSet<string>();

        var failures = new SortedDictionary<string, int>();
        int methods = 0;
        var asms = new List<Assembly> { game };
        foreach (var n in new[] { "ReLogic", "FNA", "Steamworks.NET" })
            try { asms.Add(Assembly.Load(n)); } catch (Exception e) { failures["load " + n + ": " + e.Message] = 1; }
        foreach (var asm in asms)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray()!;
                foreach (var le in e.LoaderExceptions.Where(x => x != null)) Count(failures, asm.GetName().Name + " type load: " + le!.Message);
            }
            foreach (var t in types)
            {
                if (t.ContainsGenericParameters) continue;
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                                   .Concat(t.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                    methods++;
                    try { RuntimeHelpers.PrepareMethod(m.MethodHandle); }
                    catch (Exception e) { Count(failures, asm.GetName().Name + ": " + e.GetType().Name + ": " + e.Message + "   [first in " + t.FullName + "." + m.Name + "]"); }
                }
            }
        }
        var report = new StringBuilder();
        report.AppendLine($"Compiled {methods} methods; {failures.Sum(f => f.Value)} failures in {failures.Count} kinds.");
        foreach (var u in unloadable) report.AppendLine("  unloadable: " + u);
        foreach (var f in failures) report.AppendLine($"  {f.Value,4}x {f.Key}");
        File.WriteAllText(Path.Combine(Dir, "verify64.txt"), report.ToString());
        Console.WriteLine(report);
        return failures.Count == 0 ? 0 : 2;
    }

    static void Count(SortedDictionary<string, int> d, string key)
    {
        // group by message, keeping the first location
        var k = d.Keys.FirstOrDefault(x => x.Split("   [first in")[0] == key.Split("   [first in")[0]);
        if (k == null) d[key] = 1; else d[k]++;
    }

    static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(Dir, "crash64.log"), DateTime.Now.ToString("u") + "  " + text + Environment.NewLine); } catch { }
    }
}

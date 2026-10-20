using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace TerraCompanion.Core.Game;

/// <summary>
/// Builds a 64-bit copy of the (patched) vanilla game that runs on .NET 8 with FNA instead of the 32-bit
/// .NET Framework + XNA, in a folder next to the game. Nothing of the original install is modified.
///
/// The game binary is converted, not decompiled:
///   - every XNA reference is pointed at FNA (an XNA 4 reimplementation with the same types), in Terraria and the
///     ReLogic library it embeds;
///   - the 32-bit-only flag is cleared, the embedded Steamworks.NET is replaced by a 64-bit capable one;
///   - the few Windows-only window/focus/RGB spots take the code paths vanilla already has for its Linux/Mac (FNA) builds;
///   - ReLogic uses its SDL window service and its FNA text input (both already in the vanilla library, just not wired up);
///   - two .NET Framework-only calls (delegate BeginInvoke, Thread.Abort) are replaced.
/// FNA, its native libraries (SDL2, FNA3D, FAudio...) and the 64-bit Steam API come from the user's own tModLoader
/// install, so nothing of Terraria or tModLoader is redistributed. A small .NET 8 launcher starts the converted game.
/// </summary>
public static class Port64
{
    public const string FolderName = "TerraCompanion64";
    public const string LauncherExe = "Terraria64.exe";

    static readonly string[] XnaAssemblies =
        { "Microsoft.Xna.Framework", "Microsoft.Xna.Framework.Game", "Microsoft.Xna.Framework.Graphics", "Microsoft.Xna.Framework.Xact", "Microsoft.Xna.Framework.Video" };

    /// <summary>Methods whose Windows-only window handling is switched to the code vanilla uses on its FNA platforms.</summary>
    static readonly (string type, string? method)[] NonWindowsWindowing =
    {
        ("Terraria.FocusHelper", "UpdateFocus"),
        ("Terraria.Graphics.WindowStateController", null),
        ("Terraria.Main", "IsBorderlessDisplayAvailable"),
        ("Terraria.Main", "SetDisplayMode"),
        ("Terraria.Main", "LoadSettings"),
        ("Terraria.Main", "OpenLegacySettings"),
        ("Terraria.Main", "Initialize_AlmostEverything"),
        ("Terraria.Main", "TrySupporting8K"),
        ("Terraria.Initializers.ChromaInitializer", "AddDevices"),
    };

    public static string OutputDir(string gameDir) => Path.Combine(gameDir, FolderName);

    /// <summary>The launcher needs the .NET 8 Desktop Runtime (x64).</summary>
    public static bool DesktopRuntimeInstalled()
    {
        foreach (var root in new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet") })
        {
            var dir = root == null ? null : Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
            if (dir != null && Directory.Exists(dir) && Directory.GetDirectories(dir, "8.*").Length > 0) return true;
        }
        return false;
    }

    /// <summary>The game's Terraria.exe (or the hooks) changed after the 64-bit copy was built, e.g. after a re-patch or a Steam update.</summary>
    public static bool NeedsRebuild(string gameDir)
    {
        if (!IsBuilt(gameDir)) return false;
        var built = File.GetLastWriteTimeUtc(Path.Combine(OutputDir(gameDir), "Terraria.exe"));
        var hooks = Path.Combine(gameDir, Patcher.HooksFileName);
        return File.GetLastWriteTimeUtc(Path.Combine(gameDir, "Terraria.exe")) > built || (File.Exists(hooks) && File.GetLastWriteTimeUtc(hooks) > built);
    }

    public static bool IsBuilt(string gameDir) => File.Exists(Path.Combine(OutputDir(gameDir), LauncherExe)) && File.Exists(Path.Combine(OutputDir(gameDir), "Terraria.exe"));

    /// <summary>The tModLoader install (Steam app 1281930) next to Terraria, if present.</summary>
    public static string? FindTModLoader(string gameDir)
    {
        var common = Path.GetDirectoryName(gameDir.TrimEnd('\\', '/'));
        var dir = common == null ? null : Path.Combine(common, "tModLoader");
        return dir != null && File.Exists(Path.Combine(dir, "tModLoader.dll")) ? dir : null;
    }

    sealed class Sources
    {
        public string Fna = "", SteamworksManaged = "", SteamApi64 = "", NativeDir = "";
    }

    static Sources FindSources(string tml)
    {
        string Latest(string dir, string pattern) =>
            Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).OrderByDescending(f => f).FirstOrDefault() ?? "" : "";
        var lib = Path.Combine(tml, "Libraries");
        var s = new Sources
        {
            Fna = Latest(Path.Combine(lib, "FNA"), "FNA.dll"),
            NativeDir = Path.Combine(lib, "Native", "Windows"),
        };
        var steam = Path.Combine(lib, "steamworks.net.anycpu");
        if (Directory.Exists(steam))
        {
            var ver = Directory.GetDirectories(steam).OrderByDescending(d => d).FirstOrDefault();
            if (ver != null)
            {
                s.SteamworksManaged = Path.Combine(ver, "runtimes", "win", "lib", "net8.0", "Steamworks.NET.dll");
                s.SteamApi64 = Path.Combine(ver, "runtimes", "win-x64", "native", "steam_api64.dll");
            }
        }
        foreach (var f in new[] { s.Fna, s.SteamworksManaged, s.SteamApi64, Path.Combine(s.NativeDir, "FNA3D.dll"), Path.Combine(s.NativeDir, "SDL2.dll"), Path.Combine(s.NativeDir, "FAudio.dll") })
            if (!File.Exists(f)) throw new InvalidOperationException("A file the 64-bit build needs is missing from your tModLoader install: " + f + ". Update tModLoader in Steam and try again.");
        return s;
    }

    /// <summary>
    /// Builds (or rebuilds) the 64-bit copy from the game's current Terraria.exe (patched or not).
    /// <paramref name="launcherDir"/> holds the launcher files (Terraria64.exe/.dll/.runtimeconfig.json).
    /// </summary>
    public static string Build(string gameDir, string tmlDir, string launcherDir, string? outDir = null)
    {
        var log = new List<string>();
        var src = FindSources(tmlDir);
        outDir ??= OutputDir(gameDir);
        Directory.CreateDirectory(outDir);
        string exe = Path.Combine(gameDir, "Terraria.exe");

        using var fna = AssemblyDefinition.ReadAssembly(src.Fna);
        using var steam = AssemblyDefinition.ReadAssembly(src.SteamworksManaged);
        using var resolver = new GameAssemblyResolver(exe);
        resolver.AddSearchDirectory(Path.GetDirectoryName(src.Fna)!);
        using var game = AssemblyDefinition.ReadAssembly(exe, new ReaderParameters { ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver });
        var mod = game.MainModule;

        // ---- Terraria
        log.Add($"Retargeted {Retarget(mod, fna.Name)} XNA references in Terraria to FNA.");
        log.Add(FixSteamworks(mod, steam));
        log.Add($"Adapted {AdaptToFna(mod, fna)} calls to FNA signatures.");
        log.Add($"Stubbed {StripContentPipeline(mod)} methods of the runtime .fx shader compiler (needs XNA's Windows-only content pipeline).");
        log.Add($"Switched {UseNonWindowsWindowing(mod)} window-handling checks to the FNA code paths.");
        log.Add($"Replaced {ReplaceBeginInvoke(mod)} delegate BeginInvoke calls and {RemoveThreadAbort(mod)} Thread.Abort calls (not supported on .NET 8).");
        mod.Attributes &= ~(ModuleAttributes.Required32Bit | ModuleAttributes.Preferred32Bit);

        // ---- embedded libraries
        foreach (var res in mod.Resources.OfType<EmbeddedResource>().ToList())
        {
            byte[]? replacement = null;
            if (res.Name.EndsWith(".ReLogic.dll"))
            {
                using var relogic = AssemblyDefinition.ReadAssembly(new MemoryStream(res.GetResourceData()), new ReaderParameters { ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver });
                var rm = relogic.MainModule;
                log.Add($"Retargeted {Retarget(rm, fna.Name)} XNA references in ReLogic to FNA.");
                AdaptToFna(rm, fna);
                log.Add(PatchReLogicPlatform(rm));
                rm.Attributes &= ~(ModuleAttributes.Required32Bit | ModuleAttributes.Preferred32Bit);
                var ms = new MemoryStream();
                relogic.Write(ms);
                replacement = ms.ToArray();
            }
            else if (res.Name.EndsWith(".Steamworks.NET.dll"))
            {
                replacement = File.ReadAllBytes(src.SteamworksManaged);
                log.Add("Replaced the embedded 32-bit Steamworks.NET with the 64-bit capable one from tModLoader.");
            }
            else if (res.Name.EndsWith(".dll"))
            {
                // other managed libraries marked 32-bit only (RailSDK, the WeGame SDK used only by the Chinese release): clear the flag
                // so the assembly can at least load; its native part is only touched by the WeGame code paths.
                using var lib = AssemblyDefinition.ReadAssembly(new MemoryStream(res.GetResourceData()), new ReaderParameters { ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver });
                if (!lib.MainModule.Attributes.HasFlag(ModuleAttributes.Required32Bit) || lib.MainModule.Attributes.HasFlag(ModuleAttributes.StrongNameSigned)) continue;
                lib.MainModule.Attributes &= ~(ModuleAttributes.Required32Bit | ModuleAttributes.Preferred32Bit);
                var ms = new MemoryStream();
                lib.Write(ms);
                replacement = ms.ToArray();
                log.Add($"Cleared the 32-bit-only flag of {lib.Name.Name}.");
            }
            if (replacement == null) continue;
            int i = mod.Resources.IndexOf(res);
            mod.Resources[i] = new EmbeddedResource(res.Name, res.Attributes, replacement);
        }

        game.Write(Path.Combine(outDir, "Terraria.exe"));
        log.Add("Wrote the 64-bit Terraria.");

        // ---- files next to it
        void Copy(string from, string? name = null) => File.Copy(from, Path.Combine(outDir, name ?? Path.GetFileName(from)), overwrite: true);
        Copy(src.Fna);
        Copy(src.SteamApi64);
        foreach (var f in Directory.GetFiles(src.NativeDir, "*.dll")) Copy(f);
        var hooks = Path.Combine(gameDir, Patcher.HooksFileName);
        if (File.Exists(hooks)) Copy(hooks);
        foreach (var f in Directory.GetFiles(launcherDir)) Copy(f);
        File.WriteAllText(Path.Combine(outDir, "steam_appid.txt"), "105600");
        log.Add("Copied FNA, its native libraries and the 64-bit Steam API from tModLoader, and the launcher.");

        // ---- the game's Content folder, without copying 3 GB: a directory junction
        string content = Path.Combine(outDir, "Content");
        if (!Directory.Exists(content))
        {
            CreateJunction(content, Path.Combine(gameDir, "Content"));
            log.Add("Linked the game's Content folder.");
        }
        return string.Join(Environment.NewLine, log);
    }

    /// <summary>Removes the 64-bit copy. The Content junction is removed as a link, never followed.</summary>
    public static void Remove(string gameDir)
    {
        string dir = OutputDir(gameDir);
        if (!Directory.Exists(dir)) return;
        string content = Path.Combine(dir, "Content");
        if (Directory.Exists(content))
        {
            if (!new DirectoryInfo(content).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Content in the 64-bit folder is not a link; not removing anything.");
            Directory.Delete(content, recursive: false); // deletes the junction itself
        }
        Directory.Delete(dir, recursive: true);
    }

    static void CreateJunction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0 || !Directory.Exists(link)) throw new InvalidOperationException("Could not link the Content folder: " + p.StandardError.ReadToEnd());
    }

    // ------------------------------------------------------------------ conversions

    /// <summary>Points every type reference into XNA at FNA. Returns how many assembly references were replaced.</summary>
    static int Retarget(ModuleDefinition mod, AssemblyNameDefinition fnaName)
    {
        var old = mod.AssemblyReferences.Where(r => XnaAssemblies.Contains(r.Name)).ToList();
        if (old.Count == 0) return 0;
        var fna = new AssemblyNameReference(fnaName.Name, fnaName.Version);
        mod.AssemblyReferences.Add(fna);
        foreach (var tr in mod.GetTypeReferences())
            if (tr.Scope is AssemblyNameReference a && old.Contains(a)) tr.Scope = fna;
        foreach (var r in old) mod.AssemblyReferences.Remove(r);
        return old.Count;
    }

    /// <summary>
    /// tModLoader's FNA build has a few methods with an extra trailing bool (e.g. Game(bool headless) instead of Game()).
    /// Calls to an XNA method FNA lacks, where FNA has the same method plus a trailing bool, pass false for it.
    /// </summary>
    static int AdaptToFna(ModuleDefinition mod, AssemblyDefinition fna)
    {
        var fnaRef = mod.AssemblyReferences.FirstOrDefault(a => a.Name == fna.Name.Name);
        if (fnaRef == null) return 0;
        int n = 0;
        foreach (var m in mod.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            var il = m.Body.GetILProcessor();
            foreach (var ins in m.Body.Instructions.ToList())
            {
                if (ins.Operand is not MethodReference r || r.DeclaringType.Scope != fnaRef || r.DeclaringType.IsGenericInstance) continue;
                var td = fna.MainModule.GetType(r.DeclaringType.FullName);
                if (td == null || td.Methods.Any(d => d.Name == r.Name && SameParameters(d.Parameters, r.Parameters))) continue;
                var withFlag = td.Methods.FirstOrDefault(d => d.Name == r.Name && d.Parameters.Count == r.Parameters.Count + 1
                                                              && SameParameters(d.Parameters.Take(r.Parameters.Count).ToList(), r.Parameters)
                                                              && d.Parameters.Last().ParameterType.FullName == "System.Boolean");
                if (withFlag == null) continue; // reported by the launcher's --verify if it matters
                var nr = new MethodReference(r.Name, r.ReturnType, r.DeclaringType) { HasThis = r.HasThis, ExplicitThis = r.ExplicitThis };
                foreach (var p in r.Parameters) nr.Parameters.Add(new ParameterDefinition(p.ParameterType));
                nr.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Boolean));
                il.InsertBefore(ins, il.Create(OpCodes.Ldc_I4_0));
                ins.Operand = nr;
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Terraria.Testing.FxReader compiles .fx shader source at runtime through XNA's content pipeline, which has no FNA
    /// equivalent. The game enumerates and pre-compiles every type and method of its assembly on start-up, so every trace of
    /// the pipeline must go: such methods throw NotSupportedException, pipeline types in signatures become object, and the
    /// helper classes deriving from pipeline types derive from object.
    /// </summary>
    static int StripContentPipeline(ModuleDefinition mod)
    {
        var pipe = mod.AssemblyReferences.FirstOrDefault(r => r.Name == "Microsoft.Xna.Framework.Content.Pipeline");
        if (pipe == null) return 0;
        bool Uses(TypeReference? t) => t switch
        {
            null => false,
            GenericInstanceType g => Uses(g.ElementType) || g.GenericArguments.Any(Uses),
            TypeSpecification s => Uses(s.ElementType),
            GenericParameter => false,
            _ => t.Scope == pipe || (t.DeclaringType != null && Uses(t.DeclaringType)),
        };
        bool UsesMember(object? operand) => operand switch
        {
            TypeReference t => Uses(t),
            MethodReference m => Uses(m.DeclaringType) || Uses(m.ReturnType) || m.Parameters.Any(p => Uses(p.ParameterType))
                                 || (m is GenericInstanceMethod gm && gm.GenericArguments.Any(Uses)),
            FieldReference f => Uses(f.DeclaringType) || Uses(f.FieldType),
            _ => false,
        };
        var notSupported = new TypeReference("System", "NotSupportedException", mod, mod.TypeSystem.CoreLibrary);
        var nsCtor = new MethodReference(".ctor", mod.TypeSystem.Void, notSupported) { HasThis = true };
        nsCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
        int n = 0;
        foreach (var t in mod.GetTypes())
        {
            if (Uses(t.BaseType)) t.BaseType = mod.TypeSystem.Object;
            foreach (var i in t.Interfaces.Where(i => Uses(i.InterfaceType)).ToList()) t.Interfaces.Remove(i);
            foreach (var f in t.Fields.Where(f => Uses(f.FieldType))) f.FieldType = mod.TypeSystem.Object;
            foreach (var m in t.Methods)
            {
                if (Uses(m.ReturnType)) m.ReturnType = mod.TypeSystem.Object;
                foreach (var p in m.Parameters.Where(p => Uses(p.ParameterType))) p.ParameterType = mod.TypeSystem.Object;
                foreach (var o in m.Overrides.Where(o => UsesMember(o)).ToList()) m.Overrides.Remove(o);
                if (!m.HasBody || !(m.Body.Instructions.Any(i => UsesMember(i.Operand)) || m.Body.Variables.Any(v => Uses(v.VariableType)))) continue;
                m.Body.Instructions.Clear(); m.Body.Variables.Clear(); m.Body.ExceptionHandlers.Clear();
                var il = m.Body.GetILProcessor();
                il.Emit(OpCodes.Ldstr, "Compiling .fx shaders at runtime is not available in the 64-bit build.");
                il.Emit(OpCodes.Newobj, nsCtor);
                il.Emit(OpCodes.Throw);
                n++;
            }
        }
        // a constructor whose base call went to a pipeline type now has to call object's constructor
        var objCtor = new MethodReference(".ctor", mod.TypeSystem.Void, mod.TypeSystem.Object) { HasThis = true };
        foreach (var t in mod.GetTypes().Where(t => t.BaseType?.FullName == "System.Object"))
            foreach (var c in t.Methods.Where(m => m.IsConstructor && !m.IsStatic && m.HasBody))
                foreach (var i in c.Body.Instructions)
                    if (i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == ".ctor" && Uses(r.DeclaringType)) i.Operand = objCtor;
        if (!mod.GetTypeReferences().Any(Uses)) mod.AssemblyReferences.Remove(pipe);
        return n;
    }

    static bool SameParameters(IList<ParameterDefinition> a, IList<ParameterDefinition> b) =>
        a.Count == b.Count && a.Zip(b).All(x => x.First.ParameterType.FullName == x.Second.ParameterType.FullName);

    /// <summary>The newer Steamworks.NET dropped RequestCurrentStats and added a bool to the subscribed-items calls.</summary>
    static string FixSteamworks(ModuleDefinition mod, AssemblyDefinition steam)
    {
        var steamRef = mod.AssemblyReferences.FirstOrDefault(r => r.Name == "Steamworks.NET");
        if (steamRef == null) return "No Steamworks reference.";
        steamRef.Version = steam.Name.Version;
        int fixedCalls = 0;
        foreach (var m in mod.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            var il = m.Body.GetILProcessor();
            foreach (var ins in m.Body.Instructions.ToList())
            {
                if (ins.Operand is not MethodReference r || r.DeclaringType.Scope != steamRef) continue;
                var td = steam.MainModule.GetType(r.DeclaringType.FullName);
                if (td == null || td.Methods.Any(d => d.Name == r.Name && d.Parameters.Count == r.Parameters.Count)) continue;
                if (r.Name == "RequestCurrentStats" && r.Parameters.Count == 0)
                {
                    // Newer Steam fetches the stats by itself at start-up, before the game registers its "stats received" callback,
                    // and the game then waits for that callback forever. Ask for the player's stats explicitly instead:
                    // RequestUserStats(SteamUser.GetSteamID()) fires the same callback. (Its SteamAPICall_t result is popped
                    // by the caller just like the old bool.)
                    var csteamId = new TypeReference("Steamworks", "CSteamID", mod, steamRef, valueType: true);
                    var apiCall = new TypeReference("Steamworks", "SteamAPICall_t", mod, steamRef, valueType: true);
                    var getId = new MethodReference("GetSteamID", csteamId, new TypeReference("Steamworks", "SteamUser", mod, steamRef));
                    var request = new MethodReference("RequestUserStats", apiCall, r.DeclaringType);
                    request.Parameters.Add(new ParameterDefinition(csteamId));
                    il.InsertBefore(ins, il.Create(OpCodes.Call, getId));
                    ins.Operand = request;
                    fixedCalls++;
                    continue;
                }
                var withFlag = td.Methods.FirstOrDefault(d => d.Name == r.Name && d.Parameters.Count == r.Parameters.Count + 1 && d.Parameters.Last().ParameterType.FullName == "System.Boolean");
                if (withFlag == null) throw new InvalidOperationException("Unsupported Steamworks change: " + r.FullName);
                var nr = new MethodReference(r.Name, r.ReturnType, r.DeclaringType) { HasThis = r.HasThis };
                foreach (var p in r.Parameters) nr.Parameters.Add(new ParameterDefinition(p.ParameterType));
                nr.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Boolean));
                il.InsertBefore(ins, il.Create(OpCodes.Ldc_I4_0));
                ins.Operand = nr;
                fixedCalls++;
            }
        }
        return $"Adapted {fixedCalls} Steamworks calls to the newer API.";
    }

    static int UseNonWindowsWindowing(ModuleDefinition mod)
    {
        int n = 0;
        foreach (var (typeName, method) in NonWindowsWindowing)
        {
            var type = mod.GetType(typeName) ?? throw new InvalidOperationException(typeName + " not found - unsupported game version.");
            var methods = type.Methods.Where(m => m.HasBody && (method == null || m.Name == method)).ToList();
            if (method != null && methods.Count == 0) throw new InvalidOperationException(typeName + "." + method + " not found - unsupported game version.");
            foreach (var m in methods)
                foreach (var ins in m.Body.Instructions)
                    if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference r && r.Name == "get_IsWindows" && r.DeclaringType.FullName == "ReLogic.OS.Platform")
                    {
                        ins.OpCode = OpCodes.Ldc_I4_0; ins.Operand = null;
                        n++;
                    }
        }
        return n;
    }

    /// <summary>
    /// "del.BeginInvoke(a1..an, null, null)" (not supported on .NET Core) becomes a thread-pool call of del.DynamicInvoke(a1..an),
    /// through two helper methods added to the game assembly.
    /// </summary>
    static int ReplaceBeginInvoke(ModuleDefinition mod)
    {
        var sites = mod.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.Name == "BeginInvoke"
                                                             && r.DeclaringType.Resolve()?.BaseType?.FullName == "System.MulticastDelegate").Select(i => (m, i)))
            .ToList();
        if (sites.Count == 0) return 0;

        var corlib = mod.TypeSystem.CoreLibrary;
        var objT = mod.TypeSystem.Object;
        var objArr = new ArrayType(objT);
        var delegateT = new TypeReference("System", "Delegate", mod, corlib);
        var waitCallback = new TypeReference("System.Threading", "WaitCallback", mod, corlib);
        var threadPool = new TypeReference("System.Threading", "ThreadPool", mod, corlib);

        var helpers = new TypeDefinition("TerraCompanion64", "Async", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, objT);
        mod.Types.Add(helpers);

        // static void Run(object state) { var a = (object[])state; ((Delegate)a[0]).DynamicInvoke((object[])a[1]); }
        var run = new MethodDefinition("Run", MethodAttributes.Static | MethodAttributes.Assembly | MethodAttributes.HideBySig, mod.TypeSystem.Void);
        run.Parameters.Add(new ParameterDefinition(objT));
        helpers.Methods.Add(run);
        var dyn = new MethodReference("DynamicInvoke", objT, delegateT) { HasThis = true };
        dyn.Parameters.Add(new ParameterDefinition(objArr));
        var p = run.Body.GetILProcessor();
        p.Emit(OpCodes.Ldarg_0); p.Emit(OpCodes.Castclass, objArr);
        p.Emit(OpCodes.Dup); p.Emit(OpCodes.Ldc_I4_0); p.Emit(OpCodes.Ldelem_Ref); p.Emit(OpCodes.Castclass, delegateT);
        var tmpDel = new VariableDefinition(delegateT); run.Body.Variables.Add(tmpDel);
        p.Emit(OpCodes.Stloc, tmpDel);
        p.Emit(OpCodes.Ldc_I4_1); p.Emit(OpCodes.Ldelem_Ref); p.Emit(OpCodes.Castclass, objArr);
        var tmpArgs = new VariableDefinition(objArr); run.Body.Variables.Add(tmpArgs);
        p.Emit(OpCodes.Stloc, tmpArgs);
        p.Emit(OpCodes.Ldloc, tmpDel); p.Emit(OpCodes.Ldloc, tmpArgs); p.Emit(OpCodes.Callvirt, dyn); p.Emit(OpCodes.Pop);
        p.Emit(OpCodes.Ret);

        // static void Queue(Delegate d, object[] args) { ThreadPool.QueueUserWorkItem(Run, new object[] { d, args }); }
        var queue = new MethodDefinition("Queue", MethodAttributes.Static | MethodAttributes.Assembly | MethodAttributes.HideBySig, mod.TypeSystem.Void);
        queue.Parameters.Add(new ParameterDefinition(delegateT));
        queue.Parameters.Add(new ParameterDefinition(objArr));
        helpers.Methods.Add(queue);
        var wcCtor = new MethodReference(".ctor", mod.TypeSystem.Void, waitCallback) { HasThis = true };
        wcCtor.Parameters.Add(new ParameterDefinition(objT)); wcCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.IntPtr));
        var qwi = new MethodReference("QueueUserWorkItem", mod.TypeSystem.Boolean, threadPool);
        qwi.Parameters.Add(new ParameterDefinition(waitCallback)); qwi.Parameters.Add(new ParameterDefinition(objT));
        p = queue.Body.GetILProcessor();
        p.Emit(OpCodes.Ldnull); p.Emit(OpCodes.Ldftn, run); p.Emit(OpCodes.Newobj, wcCtor);
        p.Emit(OpCodes.Ldc_I4_2); p.Emit(OpCodes.Newarr, objT);
        p.Emit(OpCodes.Dup); p.Emit(OpCodes.Ldc_I4_0); p.Emit(OpCodes.Ldarg_0); p.Emit(OpCodes.Stelem_Ref);
        p.Emit(OpCodes.Dup); p.Emit(OpCodes.Ldc_I4_1); p.Emit(OpCodes.Ldarg_1); p.Emit(OpCodes.Stelem_Ref);
        p.Emit(OpCodes.Call, qwi); p.Emit(OpCodes.Pop);
        p.Emit(OpCodes.Ret);

        foreach (var (m, call) in sites)
        {
            var r = (MethodReference)call.Operand;
            var args = r.Parameters.Take(r.Parameters.Count - 2).ToList(); // drop (AsyncCallback, object)
            var il = m.Body.GetILProcessor();
            m.Body.SimplifyMacros();
            var seq = new List<Instruction> { il.Create(OpCodes.Pop), il.Create(OpCodes.Pop) };
            var locals = args.Select(a => new VariableDefinition(a.ParameterType)).ToList();
            foreach (var v in locals) m.Body.Variables.Add(v);
            for (int k = locals.Count - 1; k >= 0; k--) seq.Add(il.Create(OpCodes.Stloc, locals[k]));
            seq.Add(il.Create(OpCodes.Ldc_I4, locals.Count));
            seq.Add(il.Create(OpCodes.Newarr, objT));
            for (int k = 0; k < locals.Count; k++)
            {
                seq.Add(il.Create(OpCodes.Dup));
                seq.Add(il.Create(OpCodes.Ldc_I4, k));
                seq.Add(il.Create(OpCodes.Ldloc, locals[k]));
                if (locals[k].VariableType.IsValueType || locals[k].VariableType.IsPrimitive) seq.Add(il.Create(OpCodes.Box, locals[k].VariableType));
                seq.Add(il.Create(OpCodes.Stelem_Ref));
            }
            seq.Add(il.Create(OpCodes.Call, queue));
            foreach (var s in seq) il.InsertBefore(call, s);
            // the original returned an IAsyncResult that the caller pops: leave a null in its place
            call.OpCode = OpCodes.Ldnull; call.Operand = null;
            m.Body.InitLocals = true;
            m.Body.OptimizeMacros();
        }
        return sites.Count;
    }

    /// <summary>Thread.Abort throws on .NET Core; the only user stops the LAN-broadcast thread, which is a background thread anyway.</summary>
    static int RemoveThreadAbort(ModuleDefinition mod)
    {
        int n = 0;
        foreach (var m in mod.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
            foreach (var ins in m.Body.Instructions)
                if (ins.Operand is MethodReference r && r.Name == "Abort" && r.Parameters.Count == 0 && r.DeclaringType.FullName == "System.Threading.Thread")
                {
                    ins.OpCode = OpCodes.Pop; ins.Operand = null;
                    n++;
                }
        return n;
    }

    /// <summary>
    /// ReLogic's Windows platform: SDL window service instead of the Win32 one, FNA text input instead of the Win32 IME hook
    /// (no window-procedure subclassing: FNA's window handle is an SDL window, not an HWND).
    /// </summary>
    static string PatchReLogicPlatform(ModuleDefinition rm)
    {
        var platform = rm.GetType("ReLogic.OS.Windows.WindowsPlatform") ?? throw new InvalidOperationException("ReLogic WindowsPlatform not found.");
        // the concrete SDL window service (SDLWindowService itself is abstract; the Linux one adds nothing platform-specific)
        var sdlWindow = rm.GetType("ReLogic.OS.Linux.WindowService")?.Methods.FirstOrDefault(m => m.IsConstructor && !m.HasParameters) ?? throw new InvalidOperationException("ReLogic SDL window service not found.");
        var fnaImeType = rm.GetType("ReLogic.Localization.IME.FnaIme") ?? throw new InvalidOperationException("ReLogic FnaIme not found.");
        var fnaIme = fnaImeType.Methods.First(m => m.IsConstructor && !m.HasParameters);

        var ctor = platform.Methods.First(m => m.IsConstructor && !m.IsStatic);
        int swapped = 0;
        foreach (var ins in ctor.Body.Instructions)
            if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference r)
            {
                if (r.DeclaringType.FullName == "ReLogic.OS.Windows.WindowService") { ins.Operand = sdlWindow; swapped++; }
                else if (r.DeclaringType.FullName == "ReLogic.Localization.IME.UnsupportedPlatformIme") { ins.Operand = fnaIme; swapped++; }
            }
        if (swapped != 2) throw new InvalidOperationException("ReLogic WindowsPlatform constructor not recognised.");

        var init = platform.Methods.First(m => m.Name == "InitializeClientServices");
        init.Body.Instructions.Clear(); init.Body.Variables.Clear(); init.Body.ExceptionHandlers.Clear();
        init.Body.GetILProcessor().Emit(OpCodes.Ret);

        // FnaIme(): TextInputEXT.TextInput += OnCharCallback; TextInputEXT.StartTextInput();
        var fnaRef = rm.AssemblyReferences.First(a => a.Name == "FNA");
        var textInput = new TypeReference("Microsoft.Xna.Framework.Input", "TextInputEXT", rm, fnaRef);
        var actionOpen = new TypeReference("System", "Action`1", rm, rm.TypeSystem.CoreLibrary);
        actionOpen.GenericParameters.Add(new GenericParameter(actionOpen));
        var actionChar = new GenericInstanceType(actionOpen);
        actionChar.GenericArguments.Add(rm.TypeSystem.Char);
        var actionCtor = new MethodReference(".ctor", rm.TypeSystem.Void, actionChar) { HasThis = true };
        actionCtor.Parameters.Add(new ParameterDefinition(rm.TypeSystem.Object)); actionCtor.Parameters.Add(new ParameterDefinition(rm.TypeSystem.IntPtr));
        var addTextInput = new MethodReference("add_TextInput", rm.TypeSystem.Void, textInput);
        addTextInput.Parameters.Add(new ParameterDefinition(actionChar));
        var start = new MethodReference("StartTextInput", rm.TypeSystem.Void, textInput);
        var onChar = fnaImeType.Methods.First(m => m.Name == "OnCharCallback");
        var il = fnaIme.Body.GetILProcessor();
        var ret = fnaIme.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
        foreach (var ins in new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldftn, onChar), il.Create(OpCodes.Newobj, actionCtor), il.Create(OpCodes.Call, addTextInput), il.Create(OpCodes.Call, start) })
            il.InsertBefore(ret, ins);
        return "ReLogic: SDL window service and FNA text input.";
    }
}

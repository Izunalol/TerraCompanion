using Mono.Cecil;

namespace TerraCompanion.Core.Game;

/// <summary>
/// Resolves Terraria's references for Cecil: the libraries Terraria embeds in itself
/// (ReLogic, Newtonsoft.Json, ...), files next to the game, and XNA from the .NET Framework GAC.
/// </summary>
sealed class GameAssemblyResolver : DefaultAssemblyResolver
{
    readonly string _exe;
    readonly Dictionary<string, AssemblyDefinition> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GameAssemblyResolver(string exe)
    {
        _exe = exe;
        AddSearchDirectory(Path.GetDirectoryName(exe)!);
    }

    public override AssemblyDefinition Resolve(AssemblyNameReference name)
    {
        if (_cache.TryGetValue(name.Name, out var cached)) return cached;
        try { return base.Resolve(name); }
        catch (AssemblyResolutionException) { }

        var found = FromEmbedded(name.Name) ?? FromGac(name.Name) ?? throw new AssemblyResolutionException(name);
        _cache[name.Name] = found;
        return found;
    }

    AssemblyDefinition? FromEmbedded(string simpleName)
    {
        using var game = AssemblyDefinition.ReadAssembly(_exe, new ReaderParameters { ReadingMode = ReadingMode.Deferred });
        var res = game.MainModule.Resources.OfType<EmbeddedResource>()
            .FirstOrDefault(r => r.Name.StartsWith("Terraria.Libraries.") && r.Name.EndsWith("." + simpleName + ".dll", StringComparison.OrdinalIgnoreCase));
        if (res == null) return null;
        return AssemblyDefinition.ReadAssembly(new MemoryStream(res.GetResourceData()), new ReaderParameters { AssemblyResolver = this });
    }

    AssemblyDefinition? FromGac(string simpleName)
    {
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var gac in new[] { "GAC_32", "GAC_MSIL", "GAC_64" })
        {
            string dir = Path.Combine(windir, "Microsoft.NET", "assembly", gac, simpleName);
            if (!Directory.Exists(dir)) continue;
            var file = Directory.EnumerateFiles(dir, simpleName + ".dll", SearchOption.AllDirectories).FirstOrDefault();
            if (file != null) return AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = this });
        }
        return null;
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var a in _cache.Values) a.Dispose();
        _cache.Clear();
        base.Dispose(disposing);
    }
}

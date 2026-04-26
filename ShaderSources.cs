using System.Collections.Concurrent;

namespace SolarSystem;

/// <summary>
/// A2: Loads GLSL shader source code from <c>Resources/Shaders/*.glsl</c> files
/// shipped alongside the binary (see csproj <c>&lt;None Include="Resources\Shaders\**\*.glsl"...&gt;</c>).
///
/// Sources are cached per name so repeated calls don't hit the disk. If the
/// file is missing the loader throws a descriptive <see cref="FileNotFoundException"/>
/// — every shader the renderer compiles must ship as a .glsl file.
/// </summary>
public static class ShaderSources
{
    private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string _root = ResolveRoot();

    private static string ResolveRoot()
    {
        // First look next to the binary (where MSBuild copies the .glsl files); fall
        // back to the source-tree layout so dotnet-run from the repo root works too.
        string baseDir = AppContext.BaseDirectory;
        string p1 = Path.Combine(baseDir, "Resources", "Shaders");
        if (Directory.Exists(p1)) return p1;
        string p2 = Path.Combine(Directory.GetCurrentDirectory(), "Resources", "Shaders");
        if (Directory.Exists(p2)) return p2;
        return p1;
    }

    /// <summary>Load the GLSL source for the given logical shader name (without extension).</summary>
    public static string Load(string name)
    {
        return _cache.GetOrAdd(name, static n =>
        {
            string path = Path.Combine(_root, n + ".glsl");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Shader source not found: {path}. Make sure Resources/Shaders/{n}.glsl ships with the build.",
                    path);
            return File.ReadAllText(path);
        });
    }

    /// <summary>Build a <see cref="ShaderProgram"/> from two named .glsl files.</summary>
    public static ShaderProgram CreateProgram(string vsName, string fsName)
        => new(Load(vsName), Load(fsName));
}

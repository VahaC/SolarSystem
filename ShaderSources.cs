using System.Collections.Concurrent;
using System.Diagnostics;

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
    {
        var prog = new ShaderProgram(Load(vsName), Load(fsName));
        // A6: register so the file watcher can swap the GL handle when the .glsl
        // file on disk changes.
        _programs.Add((vsName, fsName, new WeakReference<ShaderProgram>(prog)));
        return prog;
    }

    // ---------------- A6: GLSL hot-reload ----------------

    private static readonly List<(string vs, string fs, WeakReference<ShaderProgram> prog)> _programs = new();
    private static readonly ConcurrentQueue<string> _pendingReloads = new();
    private static FileSystemWatcher? _watcher;
    private static bool _enabled;

    /// <summary>True while a <see cref="FileSystemWatcher"/> is observing
    /// <c>Resources/Shaders/*.glsl</c> for live edits.</summary>
    public static bool HotReloadEnabled => _enabled;

    /// <summary>Toggle the file watcher on or off. When on, edits to any
    /// .glsl file under <see cref="_root"/> queue a recompile that's drained
    /// by <see cref="PollPendingReloads"/> on the GL thread.</summary>
    public static void SetHotReload(bool on)
    {
        if (on == _enabled) return;
        _enabled = on;
        if (on)
        {
            try
            {
                _watcher = new FileSystemWatcher(_root, "*.glsl")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += (_, e) => _pendingReloads.Enqueue(Path.GetFileNameWithoutExtension(e.Name ?? ""));
                _watcher.Created += (_, e) => _pendingReloads.Enqueue(Path.GetFileNameWithoutExtension(e.Name ?? ""));
                _watcher.Renamed += (_, e) => _pendingReloads.Enqueue(Path.GetFileNameWithoutExtension(e.Name ?? ""));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[hotreload] failed to start watcher: {ex.Message}");
                _enabled = false;
            }
        }
        else
        {
            try { _watcher?.Dispose(); } catch { /* ignore */ }
            _watcher = null;
            while (_pendingReloads.TryDequeue(out _)) { }
        }
    }

    /// <summary>Drain queued file-change events on the GL thread: invalidate the
    /// source cache for the changed shader and recompile every registered
    /// program whose vertex or fragment stage references it. Returns the number
    /// of programs successfully recompiled (may be 0).</summary>
    public static int PollPendingReloads(Action<string>? onSwap = null, Action<string, string>? onError = null)
    {
        if (!_enabled || _pendingReloads.IsEmpty) return 0;
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_pendingReloads.TryDequeue(out var name))
            if (!string.IsNullOrEmpty(name)) changed.Add(name);

        // Editors often write the file twice in quick succession; coalesce by
        // pausing briefly so the second write isn't seen as a half-finished file.
        Thread.Sleep(40);

        int swapped = 0;
        foreach (var name in changed) _cache.TryRemove(name, out _);
        // Compact dead weak refs while we're iterating.
        for (int i = _programs.Count - 1; i >= 0; i--)
        {
            var entry = _programs[i];
            if (!entry.prog.TryGetTarget(out var prog)) { _programs.RemoveAt(i); continue; }
            if (!changed.Contains(entry.vs) && !changed.Contains(entry.fs)) continue;
            try
            {
                prog.Reload(Load(entry.vs), Load(entry.fs));
                swapped++;
                onSwap?.Invoke($"{entry.vs}+{entry.fs}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[hotreload] {entry.vs}+{entry.fs}: {ex.Message}");
                onError?.Invoke($"{entry.vs}+{entry.fs}", ex.Message);
            }
        }
        return swapped;
    }
}

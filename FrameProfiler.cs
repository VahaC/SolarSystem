using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;

namespace SolarSystem;

/// <summary>
/// A12: Per-frame profiler. Wraps the renderer's main passes (sky, planets,
/// particles, bloom, UI) in <see cref="QueryTarget.TimeElapsed"/> queries to
/// get true GPU time per pass and a parallel <see cref="Stopwatch"/> for CPU
/// dispatch cost. Results are smoothed with an EMA so the on-screen overlay
/// doesn't flicker; queries are triple-buffered so the GL thread never stalls
/// waiting on the previous frame's result.
/// </summary>
public sealed class FrameProfiler
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Did this driver let us allocate the timer queries? On the rare GL
    /// implementation where <c>GL_TIME_ELAPSED</c> is unavailable we fall back
    /// to CPU-only numbers and the overlay simply omits the GPU column.
    /// </summary>
    public bool GpuQueriesAvailable { get; private set; } = true;

    private const int Ring = 3;
    private const int MaxPasses = 16;

    public sealed class Pass
    {
        public required string Name;
        public double GpuMs;
        public double CpuMs;
    }

    private readonly List<Pass> _passes = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly int[,] _queries = new int[MaxPasses, Ring];
    private readonly bool[,] _written = new bool[MaxPasses, Ring];
    private readonly Stopwatch _sw = new();
    private long _passStartTicks;

    private int _frameSlot;          // which ring slot we are currently filling
    private int _activePassIdx = -1; // for the active TimeElapsed query

    /// <summary>Accumulated whole-frame CPU time (Stopwatch) in ms, EMA-smoothed.</summary>
    public double FrameCpuMs { get; private set; }

    public IReadOnlyList<Pass> Passes => _passes;

    public void Initialize()
    {
        try
        {
            for (int p = 0; p < MaxPasses; p++)
                for (int r = 0; r < Ring; r++)
                    _queries[p, r] = GL.GenQuery();
        }
        catch
        {
            GpuQueriesAvailable = false;
        }
    }

    public void Shutdown()
    {
        if (!GpuQueriesAvailable) return;
        for (int p = 0; p < MaxPasses; p++)
            for (int r = 0; r < Ring; r++)
            {
                int q = _queries[p, r];
                if (q != 0) GL.DeleteQuery(q);
                _queries[p, r] = 0;
                _written[p, r] = false;
            }
    }

    public void BeginFrame()
    {
        if (!Enabled) return;
        _sw.Restart();
        // Read the slot that's least likely to still be in flight (current - 2).
        int readSlot = (_frameSlot + Ring - 2) % Ring;
        if (GpuQueriesAvailable)
        {
            for (int i = 0; i < _passes.Count; i++)
            {
                if (!_written[i, readSlot]) continue;
                int q = _queries[i, readSlot];
                GL.GetQueryObject(q, GetQueryObjectParam.QueryResultAvailable, out int avail);
                if (avail == 0) continue;
                GL.GetQueryObject(q, GetQueryObjectParam.QueryResult, out long ns);
                double ms = ns / 1_000_000.0;
                _passes[i].GpuMs = _passes[i].GpuMs * 0.85 + ms * 0.15;
            }
        }
    }

    public void BeginPass(string name)
    {
        if (!Enabled) return;
        if (_activePassIdx >= 0) EndPass();
        if (!_index.TryGetValue(name, out int idx))
        {
            if (_passes.Count >= MaxPasses) return;
            idx = _passes.Count;
            _passes.Add(new Pass { Name = name });
            _index[name] = idx;
        }
        _activePassIdx = idx;
        _passStartTicks = _sw.ElapsedTicks;
        if (GpuQueriesAvailable)
        {
            int q = _queries[idx, _frameSlot];
            GL.BeginQuery(QueryTarget.TimeElapsed, q);
        }
    }

    public void EndPass()
    {
        if (!Enabled) return;
        int idx = _activePassIdx;
        if (idx < 0) return;
        _activePassIdx = -1;
        long ticks = _sw.ElapsedTicks - _passStartTicks;
        double cpuMs = ticks * 1000.0 / Stopwatch.Frequency;
        _passes[idx].CpuMs = _passes[idx].CpuMs * 0.85 + cpuMs * 0.15;
        if (GpuQueriesAvailable)
        {
            GL.EndQuery(QueryTarget.TimeElapsed);
            _written[idx, _frameSlot] = true;
        }
    }

    public void EndFrame()
    {
        if (!Enabled) return;
        if (_activePassIdx >= 0) EndPass();
        _sw.Stop();
        double frameMs = _sw.Elapsed.TotalMilliseconds;
        FrameCpuMs = FrameCpuMs * 0.85 + frameMs * 0.15;
        _frameSlot = (_frameSlot + 1) % Ring;
    }
}

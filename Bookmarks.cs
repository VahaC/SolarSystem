using System.IO;
using System.Text.Json;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>Categorisation of a bookmark. Drives the sidebar filter rotation
/// (S12/Q8) and the colour of its tick mark on the timeline scrubber.</summary>
public enum BookmarkKind
{
    Other,
    Eclipse,
    Transit,
    Comet,
    Opposition,
    Conjunction,
    Mission,
    Discovery,
    Anniversary,
}

/// <summary>
/// S12 / Q8: Eclipse / transit / mission catalogue. Hand-curated catalogue of
/// notable Sun&ndash;Earth&ndash;Moon alignments, planetary transits, comet
/// returns, oppositions, space missions and discoveries that the user can
/// either cycle with <c>Ctrl+E</c> / <c>Ctrl+Shift+E</c> or browse via the
/// <c>F3</c> sidebar. Each bookmark snaps <c>simDays</c> to its date so
/// the configuration is reproduced in the simulation. <c>data/bookmarks.json</c>
/// (when present) overrides the built-in list, so additional events can be
/// added without recompiling.
///
/// At runtime the catalogue is grouped by <see cref="BookmarkKind"/> so the
/// active filter ("All" + every kind that actually has at least one entry)
/// can be cycled in O(1), and <see cref="Next"/> / <see cref="Prev"/> use
/// binary search across the (already date-sorted) slice — keeping the cost
/// independent of catalogue size.
/// </summary>
public sealed class Bookmarks
{
    public readonly record struct Entry(
        string Title,
        DateTime Date,
        BookmarkKind Kind,
        string Tags,
        string Where,
        double SimDays);

    private Entry[] _all = Array.Empty<Entry>();
    private readonly Dictionary<BookmarkKind, Entry[]> _byKind = new();
    /// <summary>Active filter rotation. Index 0 is the <c>null</c> sentinel
    /// "All"; subsequent entries are the kinds that actually appear in the
    /// loaded catalogue, in <see cref="BookmarkKind"/> declaration order.</summary>
    private BookmarkKind?[] _filterOrder = Array.Empty<BookmarkKind?>();
    private int _filterIdx;

    public Bookmarks()
    {
        var entries = TryLoadFromJson() ?? BuildDefaults();
        Initialise(entries);
    }

    public IReadOnlyList<Entry> All => _all;

    /// <summary>The active filter, or <c>null</c> for "All".</summary>
    public BookmarkKind? ActiveFilter
        => _filterOrder.Length == 0 ? null : _filterOrder[_filterIdx];

    public IReadOnlyList<BookmarkKind?> AvailableFilters => _filterOrder;

    /// <summary>Entries visible under the current filter, sorted by date.</summary>
    public IReadOnlyList<Entry> Filtered
    {
        get
        {
            var f = ActiveFilter;
            if (f is null) return _all;
            return _byKind.TryGetValue(f.Value, out var arr) ? arr : Array.Empty<Entry>();
        }
    }

    /// <summary>Index of the bookmark most recently jumped to within the
    /// active filter, or -1.</summary>
    public int Current { get; private set; } = -1;

    /// <summary>Advance to the next bookmark whose date is after
    /// <paramref name="afterSimDays"/>; wraps around to the first entry when
    /// none is found.</summary>
    public Entry? Next(double afterSimDays)
    {
        var arr = Filtered;
        if (arr.Count == 0) return null;
        int idx = UpperBound(arr, afterSimDays + 0.5);
        if (idx >= arr.Count) idx = 0;
        Current = idx;
        return arr[idx];
    }

    /// <summary>Step backward to the previous bookmark whose date is before
    /// <paramref name="beforeSimDays"/>; wraps to the last entry.</summary>
    public Entry? Prev(double beforeSimDays)
    {
        var arr = Filtered;
        if (arr.Count == 0) return null;
        int idx = LowerBound(arr, beforeSimDays - 0.5) - 1;
        if (idx < 0) idx = arr.Count - 1;
        Current = idx;
        return arr[idx];
    }

    /// <summary>Cycle the active filter by <paramref name="direction"/>
    /// (+1 or -1). The "All" pseudo-filter is always present so this never
    /// becomes a no-op.</summary>
    public void CycleFilter(int direction)
    {
        if (_filterOrder.Length == 0) return;
        int n = _filterOrder.Length;
        _filterIdx = ((_filterIdx + direction) % n + n) % n;
        Current = -1;
    }

    /// <summary>Up to <paramref name="max"/> events strictly after
    /// <paramref name="simDays"/> from the active filter, in date order.
    /// Used by the sidebar to show what's coming up next.</summary>
    public IEnumerable<Entry> Upcoming(double simDays, int max)
    {
        var arr = Filtered;
        int idx = UpperBound(arr, simDays);
        for (int i = idx; i < arr.Count && i - idx < max; i++) yield return arr[i];
    }

    /// <summary>Up to <paramref name="max"/> events strictly before
    /// <paramref name="simDays"/> from the active filter, most recent first.</summary>
    public IEnumerable<Entry> Recent(double simDays, int max)
    {
        var arr = Filtered;
        int idx = LowerBound(arr, simDays) - 1;
        for (int i = idx, n = 0; i >= 0 && n < max; i--, n++) yield return arr[i];
    }

    public static double ToSimDays(in Entry e) => e.SimDays;

    public static string KindLabel(BookmarkKind? k) => k?.ToString() ?? "All";

    /// <summary>Per-kind tint for the timeline tick marks and sidebar bullets.
    /// Picked to read clearly against both the dark sky and the lit
    /// scrubber bar.</summary>
    public static Vector4 KindColor(BookmarkKind? k) => k switch
    {
        BookmarkKind.Eclipse     => new Vector4(1.00f, 0.55f, 0.20f, 1f),
        BookmarkKind.Transit     => new Vector4(0.40f, 0.80f, 1.00f, 1f),
        BookmarkKind.Comet       => new Vector4(0.65f, 1.00f, 0.85f, 1f),
        BookmarkKind.Opposition  => new Vector4(1.00f, 0.85f, 0.40f, 1f),
        BookmarkKind.Conjunction => new Vector4(0.85f, 0.65f, 1.00f, 1f),
        BookmarkKind.Mission     => new Vector4(0.50f, 1.00f, 0.50f, 1f),
        BookmarkKind.Discovery   => new Vector4(1.00f, 0.50f, 0.85f, 1f),
        BookmarkKind.Anniversary => new Vector4(0.85f, 0.85f, 0.85f, 1f),
        BookmarkKind.Other       => new Vector4(0.80f, 0.85f, 1.00f, 1f),
        _                        => new Vector4(1.00f, 1.00f, 1.00f, 1f),
    };

    public static BookmarkKind ParseKind(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return BookmarkKind.Other;
        return Enum.TryParse<BookmarkKind>(s, ignoreCase: true, out var k) ? k : BookmarkKind.Other;
    }

    private void Initialise(List<Entry> entries)
    {
        // Re-evaluate SimDays in case the JSON loader didn't (or the defaults
        // were built without it) so the binary search keys are always valid.
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.SimDays == 0.0)
                entries[i] = e with { SimDays = (e.Date - OrbitalMechanics.J2000).TotalDays };
        }
        entries.Sort((a, b) => a.SimDays.CompareTo(b.SimDays));
        _all = entries.ToArray();

        _byKind.Clear();
        foreach (var grp in _all.GroupBy(e => e.Kind))
            _byKind[grp.Key] = grp.OrderBy(e => e.SimDays).ToArray();

        // Filter rotation: "All" first, then each kind in declaration order
        // that actually has at least one event.
        var order = new List<BookmarkKind?> { null };
        foreach (BookmarkKind k in Enum.GetValues<BookmarkKind>())
            if (_byKind.ContainsKey(k)) order.Add(k);
        _filterOrder = order.ToArray();
        _filterIdx = 0;
    }

    private static int UpperBound(IReadOnlyList<Entry> arr, double key)
    {
        int lo = 0, hi = arr.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (arr[mid].SimDays <= key) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static int LowerBound(IReadOnlyList<Entry> arr, double key)
    {
        int lo = 0, hi = arr.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (arr[mid].SimDays < key) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static List<Entry>? TryLoadFromJson()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "data", "bookmarks.json");
            if (!File.Exists(path))
            {
                path = Path.Combine("data", "bookmarks.json");
                if (!File.Exists(path)) return null;
            }
            using var s = File.OpenRead(path);
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var raw = JsonSerializer.Deserialize<JsonEntry[]>(s, opts);
            if (raw == null || raw.Length == 0) return null;
            var list = new List<Entry>(raw.Length);
            foreach (var r in raw)
            {
                if (string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(r.Date)) continue;
                if (!DateTime.TryParse(r.Date,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var dt))
                    continue;
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                var kind = ParseKind(r.Kind);
                double sim = (dt - OrbitalMechanics.J2000).TotalDays;
                list.Add(new Entry(r.Title!, dt, kind, r.Tags ?? "", r.Where ?? "", sim));
            }
            return list.Count > 0 ? list : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Compact built-in fallback when no <c>bookmarks.json</c> ships.
    /// The JSON catalogue is the canonical, much larger source.</summary>
    private static List<Entry> BuildDefaults()
    {
        static Entry E(string t, int y, int mo, int d, BookmarkKind k, string tags = "", string where = "")
        {
            var dt = new DateTime(y, mo, d, 12, 0, 0, DateTimeKind.Utc);
            return new Entry(t, dt, k, tags, where, (dt - OrbitalMechanics.J2000).TotalDays);
        }
        return new List<Entry>
        {
            E("Total solar eclipse",      1999,  8, 11, BookmarkKind.Eclipse, "solar,total", "EU/ME"),
            E("Total solar eclipse (US)", 2017,  8, 21, BookmarkKind.Eclipse, "solar,total", "USA"),
            E("Total solar eclipse (NA)", 2024,  4,  8, BookmarkKind.Eclipse, "solar,total", "MX/US/CA"),
            E("Total solar eclipse",      2027,  8,  2, BookmarkKind.Eclipse, "solar,total", "ES/SA"),
            E("Venus transit",            2004,  6,  8, BookmarkKind.Transit, "venus"),
            E("Venus transit (last 2117)",2012,  6,  6, BookmarkKind.Transit, "venus"),
            E("Mercury transit",          2016,  5,  9, BookmarkKind.Transit, "mercury"),
            E("Mercury transit",          2032, 11, 13, BookmarkKind.Transit, "mercury"),
            E("Halley perihelion",        1986,  2,  9, BookmarkKind.Comet,   "halley"),
            E("Halley perihelion (next)", 2061,  7, 28, BookmarkKind.Comet,   "halley"),
            E("Hale-Bopp perihelion",     1997,  4,  1, BookmarkKind.Comet,   "hale-bopp"),
            E("Mars opposition (closest)",2003,  8, 28, BookmarkKind.Opposition,"mars"),
            E("Apollo 11 Moon landing",   1969,  7, 20, BookmarkKind.Mission, "apollo"),
            E("Voyager 1 launch",         1977,  9,  5, BookmarkKind.Mission, "voyager"),
            E("Pluto flyby (New Horizons)",2015, 7, 14, BookmarkKind.Mission, "new-horizons"),
            E("J2000 epoch",              2000,  1,  1, BookmarkKind.Anniversary, "epoch"),
        };
    }

    private sealed class JsonEntry
    {
        public string? Title { get; set; }
        public string? Date { get; set; }
        public string? Kind { get; set; }
        public string? Tags { get; set; }
        public string? Where { get; set; }
    }
}

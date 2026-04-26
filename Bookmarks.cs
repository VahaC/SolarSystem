namespace SolarSystem;

/// <summary>
/// S12: Eclipse / transit calendar. Static catalogue of notable Sun&ndash;Earth&ndash;Moon
/// alignments and Mercury / Venus transits that the user can cycle with
/// <c>Ctrl+B</c>; each bookmark snaps <c>simDays</c> to its date so the
/// configuration is reproduced in the simulation. Dates come from NASA's eclipse
/// and transit pages; they're just data &mdash; no orbital pre-computation
/// happens at runtime.
/// </summary>
public sealed class Bookmarks
{
    public readonly record struct Entry(string Title, DateTime Date, string Kind);

    private readonly Entry[] _entries =
    {
        new Entry("Total solar eclipse",        new DateTime(1999,  8, 11, 11, 0, 0, DateTimeKind.Utc), "Eclipse"),
        new Entry("Venus transit",              new DateTime(2004,  6,  8,  8, 20, 0, DateTimeKind.Utc), "Transit"),
        new Entry("Total solar eclipse",        new DateTime(2006,  3, 29, 10,  0, 0, DateTimeKind.Utc), "Eclipse"),
        new Entry("Total lunar eclipse",        new DateTime(2008,  2, 21,  3, 26, 0, DateTimeKind.Utc), "Eclipse"),
        new Entry("Total solar eclipse",        new DateTime(2009,  7, 22,  2, 35, 0, DateTimeKind.Utc), "Eclipse"),
        new Entry("Venus transit (last 2117)",  new DateTime(2012,  6,  6,  1, 30, 0, DateTimeKind.Utc), "Transit"),
        new Entry("Mercury transit",            new DateTime(2016,  5,  9, 14, 57, 0, DateTimeKind.Utc), "Transit"),
        new Entry("Total solar eclipse (US)",   new DateTime(2017,  8, 21, 18, 25, 0, DateTimeKind.Utc), "Eclipse"),
        new Entry("Mercury transit",            new DateTime(2019, 11, 11, 15, 20, 0, DateTimeKind.Utc), "Transit"),
        new Entry("Total solar eclipse (NA)",   new DateTime(2024,  4,  8, 18, 17, 0, DateTimeKind.Utc), "Eclipse"),
        new Entry("Total lunar eclipse",        new DateTime(2025,  3, 14,  6, 58, 0, DateTimeKind.Utc), "Eclipse"),
        new Entry("Annular solar eclipse",      new DateTime(2027,  2,  6, 16,  0, 0, DateTimeKind.Utc), "Eclipse"),
        new Entry("Total solar eclipse",        new DateTime(2027,  8,  2, 10,  7, 0, DateTimeKind.Utc), "Eclipse"),
        new Entry("Mercury transit",            new DateTime(2032, 11, 13,  8, 54, 0, DateTimeKind.Utc), "Transit"),
        new Entry("Total solar eclipse",        new DateTime(2045,  8, 12, 17, 42, 0, DateTimeKind.Utc), "Eclipse"),
    };

    public IReadOnlyList<Entry> All => _entries;

    /// <summary>Index of the bookmark most recently jumped to, or -1.</summary>
    public int Current { get; private set; } = -1;

    /// <summary>Advance to the next bookmark whose date is after <paramref name="afterSimDays"/>;
    /// wraps around to the first entry when none is found. Returns the chosen entry
    /// (and updates <see cref="Current"/>), or <c>null</c> if the catalogue is empty.</summary>
    public Entry? Next(double afterSimDays)
    {
        if (_entries.Length == 0) return null;
        for (int i = 0; i < _entries.Length; i++)
        {
            double d = (_entries[i].Date - OrbitalMechanics.J2000).TotalDays;
            if (d > afterSimDays + 0.5)
            {
                Current = i;
                return _entries[i];
            }
        }
        Current = 0;
        return _entries[0];
    }

    /// <summary>Convert an entry's date to <c>simDays</c> (days since J2000).</summary>
    public static double ToSimDays(in Entry e)
        => (e.Date - OrbitalMechanics.J2000).TotalDays;
}

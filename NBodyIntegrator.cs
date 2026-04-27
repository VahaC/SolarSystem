using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// S15: optional N-body perturbation mode. When enabled, the major planets are
/// advanced by a velocity-Verlet (kick-drift-kick leapfrog) integrator that
/// includes mutual gravity in addition to the Sun, so e.g. Jupiter's secular
/// pull on Mars becomes visible over decadal time-scales. Units: AU, days,
/// solar masses (so GM_sun = 4π² AU³/yr² ≈ 2.959e-4 AU³/day²). The Sun is
/// fixed at the origin — its barycentric wobble is below the rendering noise
/// floor at the scales we care about.
///
/// The integrator is spun up from the analytic Kepler state of every major
/// planet, then takes over per-frame updates. Whenever a large sim-time jump
/// occurs (date seek, scrubber drag, bookmark cycle, scale toggle) the caller
/// flags the integrator dirty and a fresh resync is performed on the next
/// frame so chronic drift never accumulates.
/// </summary>
public sealed class NBodyIntegrator
{
    /// <summary>GM_sun in AU³/day²: 4π² / 365.25² ≈ 2.959122e-4.</summary>
    private const double GM_SUN = 2.959122082855911e-4;

    /// <summary>Maximum sub-step in days. Smaller = more accurate but more work.
    /// 0.5 d gives perceptibly smooth Jupiter-on-Mars perturbation while
    /// keeping a frame at high speeds inside ~milliseconds of CPU.</summary>
    public double MaxSubStepDays { get; set; } = 0.5;
    /// <summary>Hard cap on sub-step count per frame so a runaway speed setting
    /// can't lock the UI thread.</summary>
    public int MaxSubStepsPerFrame { get; set; } = 200;

    /// <summary>Approximate solar masses for each major planet, keyed by name.
    /// Bodies missing from the table contribute only their Sun-attracted motion
    /// (mass = 0); used as a graceful fallback for renamed / dwarf entries.</summary>
    private static readonly Dictionary<string, double> Masses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mercury"] = 1.6601367952719e-7,
        ["Venus"]   = 2.4478383396645e-6,
        ["Earth"]   = 3.0034896149157e-6,
        ["Mars"]    = 3.2271514350444e-7,
        ["Jupiter"] = 9.5479194122589e-4,
        ["Saturn"]  = 2.8588598066345e-4,
        ["Uranus"]  = 4.3662440433515e-5,
        ["Neptune"] = 5.1513890244706e-5,
    };

    private Vector3d[] _x = Array.Empty<Vector3d>();
    private Vector3d[] _v = Array.Empty<Vector3d>();
    private Vector3d[] _a = Array.Empty<Vector3d>();
    private double[] _m = Array.Empty<double>();
    private double _lastDays;
    private bool _ready;

    public bool Ready => _ready;

    /// <summary>Snapshot every major planet's analytic Kepler state at
    /// <paramref name="simDays"/> and prime the integrator. Velocities are taken
    /// from a 1-day central finite difference of the analytic position.</summary>
    public void Resync(Planet[] majors, double simDays)
    {
        int n = majors.Length;
        if (_x.Length != n)
        {
            _x = new Vector3d[n];
            _v = new Vector3d[n];
            _a = new Vector3d[n];
            _m = new double[n];
        }
        for (int i = 0; i < n; i++)
        {
            var p = majors[i];
            var x  = OrbitalMechanics.HeliocentricPosition(p, simDays);
            var xM = OrbitalMechanics.HeliocentricPosition(p, simDays - 0.5);
            var xP = OrbitalMechanics.HeliocentricPosition(p, simDays + 0.5);
            _x[i] = x;
            _v[i] = xP - xM; // AU/day
            _m[i] = Masses.TryGetValue(p.Name, out var m) ? m : 0.0;
            p.HelioAU = x;
        }
        _lastDays = simDays;
        _ready = true;
    }

    /// <summary>Advance the integrator from <see cref="_lastDays"/> to
    /// <paramref name="simDaysNow"/> using kick-drift-kick leapfrog, then write
    /// the new positions back into each planet's <see cref="Planet.HelioAU"/>.
    /// Time can flow backward (negative dt) — leapfrog is symplectic and handles it.</summary>
    public void Step(Planet[] majors, double simDaysNow)
    {
        if (!_ready) { Resync(majors, simDaysNow); return; }
        int n = _x.Length;
        if (majors.Length != n) { Resync(majors, simDaysNow); return; }

        double dt = simDaysNow - _lastDays;
        if (dt == 0)
        {
            // No time elapsed — still write current positions through.
            for (int i = 0; i < n; i++) majors[i].HelioAU = _x[i];
            return;
        }

        int sub = (int)Math.Ceiling(Math.Abs(dt) / MaxSubStepDays);
        if (sub < 1) sub = 1;
        if (sub > MaxSubStepsPerFrame) sub = MaxSubStepsPerFrame;
        double h = dt / sub;

        ComputeAcceleration();
        for (int k = 0; k < sub; k++)
        {
            // Kick (½ dt)
            for (int i = 0; i < n; i++) _v[i] += _a[i] * (h * 0.5);
            // Drift
            for (int i = 0; i < n; i++) _x[i] += _v[i] * h;
            // Recompute acceleration at new positions
            ComputeAcceleration();
            // Kick (½ dt)
            for (int i = 0; i < n; i++) _v[i] += _a[i] * (h * 0.5);
        }

        for (int i = 0; i < n; i++) majors[i].HelioAU = _x[i];
        _lastDays = simDaysNow;
    }

    private void ComputeAcceleration()
    {
        int n = _x.Length;
        for (int i = 0; i < n; i++)
        {
            var ri = _x[i];
            double r2 = ri.X * ri.X + ri.Y * ri.Y + ri.Z * ri.Z;
            double r  = Math.Sqrt(r2);
            double inv = 1.0 / (r2 * r);
            // Sun gravity (Sun fixed at origin).
            var ai = -ri * (GM_SUN * inv);

            // Mutual gravity from every other major.
            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;
                double mj = _m[j];
                if (mj <= 0) continue;
                var rij = _x[j] - ri;
                double d2 = rij.X * rij.X + rij.Y * rij.Y + rij.Z * rij.Z;
                double d  = Math.Sqrt(d2);
                double k  = GM_SUN * mj / (d2 * d);
                ai += rij * k;
            }
            _a[i] = ai;
        }
    }
}

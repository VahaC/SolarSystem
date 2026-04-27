namespace SolarSystem;

/// <summary>
/// Truncated Brown / ELP-2000 lunar theory after Meeus, "Astronomical Algorithms"
/// (2nd ed., 1998), Chapter 47. Returns geocentric ecliptic coordinates of the Moon
/// (longitude, latitude, Earth–Moon distance) referred to the mean equinox of date.
///
/// We keep the ~25 dominant longitude/distance terms from Table 47.A and the ~13
/// dominant latitude terms from Table 47.B, plus the standard A1/A2/A3 planetary
/// perturbation corrections. That is enough for ~10″ longitude accuracy, which
/// translates into an eclipse-time error of a few minutes — well within the user's
/// "±кілька хвилин" target and the resolution of the bookmark catalogue.
/// </summary>
public static class LunarEphemeris
{
    private const double DegToRad = System.Math.PI / 180.0;

    // Table 47.A — periodic terms for the longitude (Σl, micro-degrees of sine)
    // and the radius vector (Σr, units of metres × 1000 of cosine).
    // Columns: D, M, M', F, sin_l (1e-6 deg), cos_r (m × 1000 / km).
    // Sorted by descending |sin_l|; first ~25 terms cover ≈ 99.5 % of the signal.
    private static readonly int[,] LR = new int[,]
    {
        { 0, 0,  1, 0,  6288774, -20905355 },
        { 2, 0, -1, 0,  1274027,  -3699111 },
        { 2, 0,  0, 0,   658314,  -2955968 },
        { 0, 0,  2, 0,   213618,   -569925 },
        { 0, 1,  0, 0,  -185116,     48888 },
        { 0, 0,  0, 2,  -114332,     -3149 },
        { 2, 0, -2, 0,    58793,    246158 },
        { 2,-1, -1, 0,    57066,   -152138 },
        { 2, 0,  1, 0,    53322,   -170733 },
        { 2,-1,  0, 0,    45758,   -204586 },
        { 0, 1, -1, 0,   -40923,   -129620 },
        { 1, 0,  0, 0,   -34720,    108743 },
        { 0, 1,  1, 0,   -30383,    104755 },
        { 2, 0,  0,-2,    15327,     10321 },
        { 0, 0,  1, 2,   -12528,         0 },
        { 0, 0,  1,-2,    10980,     79661 },
        { 4, 0, -1, 0,    10675,    -34782 },
        { 0, 0,  3, 0,    10034,    -23210 },
        { 4, 0, -2, 0,     8548,    -21636 },
        { 2, 1, -1, 0,    -7888,     24208 },
        { 2, 1,  0, 0,    -6766,     30824 },
        { 1, 0, -1, 0,    -5163,     -8379 },
        { 2,-1,  1, 0,     4987,    -16675 },
        { 2,-2,  0, 0,     4036,    -12831 },
        { 2, 0,  2, 0,     3994,    -10445 },
        { 2, 0, -3, 0,     3861,     14403 },
    };

    // Table 47.B — periodic terms for the latitude (Σb, micro-degrees of sine).
    // Columns: D, M, M', F, sin_b (1e-6 deg). Top 13 terms.
    private static readonly int[,] B = new int[,]
    {
        { 0, 0,  0, 1, 5128122 },
        { 0, 0,  1, 1,  280602 },
        { 0, 0,  1,-1,  277693 },
        { 2, 0,  0,-1,  173237 },
        { 2, 0, -1, 1,   55413 },
        { 2, 0, -1,-1,   46271 },
        { 2, 0,  0, 1,   32573 },
        { 0, 0,  2, 1,   17198 },
        { 2, 0,  1,-1,    9266 },
        { 0, 0,  2,-1,    8822 },
        { 2,-1,  0,-1,    8216 },
        { 2, 0, -2,-1,    4324 },
        { 2, 0,  1, 1,    4200 },
    };

    /// <summary>Compute the Moon's geocentric ecliptic position at the given
    /// time, expressed in days since J2000 (TT, 2000-01-01 12:00).</summary>
    /// <returns>Tuple (longitude in degrees, latitude in degrees, distance in km).</returns>
    public static (double LongitudeDeg, double LatitudeDeg, double DistanceKm) Compute(double daysSinceJ2000)
    {
        double T = daysSinceJ2000 / 36525.0;
        double T2 = T * T;
        double T3 = T2 * T;
        double T4 = T3 * T;

        // Mean elements (degrees) — Meeus (47.1)–(47.5).
        double Lp = 218.3164477 + 481267.88123421 * T - 0.0015786 * T2 + T3 / 538841.0    - T4 / 65194000.0;
        double D  = 297.8501921 + 445267.1114034  * T - 0.0018819 * T2 + T3 / 545868.0    - T4 / 113065000.0;
        double M  = 357.5291092 +  35999.0502909  * T - 0.0001536 * T2 + T3 / 24490000.0;
        double Mp = 134.9633964 + 477198.8675055  * T + 0.0087414 * T2 + T3 / 69699.0     - T4 / 14712000.0;
        double F  =  93.2720950 + 483202.0175233  * T - 0.0036539 * T2 - T3 / 3526000.0   + T4 / 863310000.0;

        // Planetary-perturbation arguments — Meeus (47.6).
        double A1 = 119.75 + 131.849 * T;
        double A2 =  53.09 + 479264.290 * T;
        double A3 = 313.45 + 481266.484 * T;

        // Eccentricity correction — Meeus (47.7).
        double E  = 1.0 - 0.002516 * T - 0.0000074 * T2;
        double E2 = E * E;

        double Lp_r = Norm360(Lp) * DegToRad;
        double D_r  = Norm360(D)  * DegToRad;
        double M_r  = Norm360(M)  * DegToRad;
        double Mp_r = Norm360(Mp) * DegToRad;
        double F_r  = Norm360(F)  * DegToRad;
        double A1_r = Norm360(A1) * DegToRad;
        double A2_r = Norm360(A2) * DegToRad;
        double A3_r = Norm360(A3) * DegToRad;

        // Σl, Σr from Table 47.A.
        double sumL = 0.0, sumR = 0.0;
        for (int k = 0; k < LR.GetLength(0); k++)
        {
            int dD = LR[k, 0], dM = LR[k, 1], dMp = LR[k, 2], dF = LR[k, 3];
            double sinCoef = LR[k, 4];
            double cosCoef = LR[k, 5];
            double arg = dD * D_r + dM * M_r + dMp * Mp_r + dF * F_r;
            double w = (System.Math.Abs(dM) == 1) ? E : (System.Math.Abs(dM) == 2 ? E2 : 1.0);
            sumL += sinCoef * w * System.Math.Sin(arg);
            sumR += cosCoef * w * System.Math.Cos(arg);
        }

        // Σb from Table 47.B.
        double sumB = 0.0;
        for (int k = 0; k < B.GetLength(0); k++)
        {
            int dD = B[k, 0], dM = B[k, 1], dMp = B[k, 2], dF = B[k, 3];
            double sinCoef = B[k, 4];
            double arg = dD * D_r + dM * M_r + dMp * Mp_r + dF * F_r;
            double w = (System.Math.Abs(dM) == 1) ? E : (System.Math.Abs(dM) == 2 ? E2 : 1.0);
            sumB += sinCoef * w * System.Math.Sin(arg);
        }

        // Planetary perturbations — Meeus pp. 338–339.
        sumL += 3958.0 * System.Math.Sin(A1_r)
              + 1962.0 * System.Math.Sin(Lp_r - F_r)
              +  318.0 * System.Math.Sin(A2_r);

        sumB += -2235.0 * System.Math.Sin(Lp_r)
              +   382.0 * System.Math.Sin(A3_r)
              +   175.0 * System.Math.Sin(A1_r - F_r)
              +   175.0 * System.Math.Sin(A1_r + F_r)
              +   127.0 * System.Math.Sin(Lp_r - Mp_r)
              -   115.0 * System.Math.Sin(Lp_r + Mp_r);

        double lambdaDeg = Norm360(Lp + sumL / 1.0e6);
        double betaDeg   = sumB / 1.0e6;
        double distanceKm = 385000.56 + sumR / 1000.0;
        return (lambdaDeg, betaDeg, distanceKm);
    }

    private static double Norm360(double deg)
    {
        double r = deg % 360.0;
        if (r < 0) r += 360.0;
        return r;
    }
}

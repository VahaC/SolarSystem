namespace SolarSystem;

/// <summary>
/// Low-precision theory of the Galilean satellites of Jupiter, after Meeus,
/// "Astronomical Algorithms" (2nd ed., 1998), Chapter 44. Returns each moon's
/// planetocentric mean longitude (u₁..u₄, degrees) measured from the line
/// Jupiter→Earth in Jupiter's equatorial plane. That is exactly the angle our
/// renderer needs to drive the moon's position around its host: superior
/// conjunction (u = 0°) places the moon behind Jupiter, inferior conjunction
/// (u = 180°) places it in front, so transits across the disk and shadow casts
/// onto the cloud tops happen at the historically correct dates.
/// </summary>
public static class GalileanEphemeris
{
    private const double DegToRad = System.Math.PI / 180.0;

    /// <summary>Mean longitudes u₁..u₄ of Io / Europa / Ganymede / Callisto in
    /// Jupiter's equatorial plane, measured from superior conjunction.</summary>
    public static (double Io, double Europa, double Ganymede, double Callisto) MeanLongitudes(double daysSinceJ2000)
    {
        // Meeus uses days from epoch 1989-Aug-16.0 TT (JD 2447800.0). J2000 is
        // JD 2451545.0, so d_meeus = days_since_J2000 + 3745.0.
        double d = daysSinceJ2000 + 3745.0;

        double V = Norm360(172.74 + 0.00111588 * d);
        double M = Norm360(357.529 + 0.9856003  * d);

        double sinV = System.Math.Sin(V * DegToRad);
        double N = Norm360( 20.020 + 0.0830853 * d + 0.329 * sinV);
        double J = Norm360( 66.115 + 0.9025179 * d - 0.329 * sinV);

        double sinM  = System.Math.Sin(M * DegToRad);
        double sin2M = System.Math.Sin(2.0 * M * DegToRad);
        double sinN  = System.Math.Sin(N * DegToRad);
        double sin2N = System.Math.Sin(2.0 * N * DegToRad);

        double A = 1.915 * sinM + 0.020 * sin2M;
        double B_corr = 5.555 * sinN + 0.168 * sin2N;
        double K = J + A - B_corr;

        double cosM  = System.Math.Cos(M  * DegToRad);
        double cos2M = System.Math.Cos(2.0 * M * DegToRad);
        double cosN  = System.Math.Cos(N  * DegToRad);
        double cos2N = System.Math.Cos(2.0 * N * DegToRad);
        double R_earth = 1.00014 - 0.01671 * cosM - 0.00014 * cos2M;
        double r_jup   = 5.20872 - 0.25208 * cosN - 0.00611 * cos2N;
        double cosK = System.Math.Cos(K * DegToRad);
        double Delta = System.Math.Sqrt(R_earth * R_earth + r_jup * r_jup - 2.0 * R_earth * r_jup * cosK);
        double psi = System.Math.Asin(R_earth / Delta * System.Math.Sin(K * DegToRad)) / DegToRad;

        // Light-time correction τ = Δ / 173 days.
        double dt = d - Delta / 173.0;
        double psiMinusB = psi - B_corr;

        double u1 = Norm360(163.8067 + 203.4058643 * dt + psiMinusB);
        double u2 = Norm360(358.4108 + 101.2916334 * dt + psiMinusB);
        double u3 = Norm360(  5.7129 +  50.2345179 * dt + psiMinusB);
        double u4 = Norm360(224.8151 +  21.4879801 * dt + psiMinusB);
        return (u1, u2, u3, u4);
    }

    private static double Norm360(double deg)
    {
        double r = deg % 360.0;
        if (r < 0) r += 360.0;
        return r;
    }
}

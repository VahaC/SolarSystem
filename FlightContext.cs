using OpenTK.Mathematics;

namespace SolarSystem;

// PROTOTYPE (2026-11-17): arcade flight mode — the shared contract between the flight
// model, the ship renderer, the ship effects, the rock field and the window glue.
//
// Conventions everything in flight mode follows:
//  * World units are the compressed ("artistic") scale: the Sun sits at the origin with
//    radius 5, Saturn has radius 3.4 and its ring spans 4.59 .. 8.16 from its centre.
//  * Ship local space: forward = -Z, up = +Y, right = +X. The hull is one local unit
//    long (nose at z = -0.5, engine exits at z = +0.5) and is scaled to world units by
//    ShipLayout.Length. See ShipLayout for the fixed points other systems rely on.
//  * Matrices use OpenTK's row-vector convention: world = local * Model, with
//    Model = Scale * Rotation * Translation. Pass them with ShaderProgram.SetMatrix4;
//    shaders compute uProj * uView * uModel * vec4(p, 1).
//  * The scene is HDR (RGBA16F) with bloom: colour values above 1 glow. The Sun is the
//    only light (FlightContext.SunPosition).
//  * Every 3D vertex shader writes logarithmic depth exactly like orbit.vert.glsl:
//      gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
//    with uFcoef = 2 / log2(cam.Far + 1).

/// <summary>Player (or autopilot) controls for one frame, each axis in -1 .. +1.</summary>
public struct ShipInput
{
    /// <summary>+1 = nose up.</summary>
    public float Pitch;
    /// <summary>+1 = turn right.</summary>
    public float Yaw;
    /// <summary>+1 = roll right (clockwise seen from behind).</summary>
    public float Roll;
    public bool Boost;
    public bool Brake;
}

/// <summary>
/// Mouse steering: while the left button is held, the cursor's offset from the point
/// where it was pressed works like a joystick — drag right to turn right, drag up to
/// raise the nose; release (or return to the press point) to fly straight.
/// </summary>
public static class MouseStick
{
    /// <summary>Fraction of the stick radius around the press point that does nothing,
    /// so a click without a drag does not twitch the ship.</summary>
    public const float DeadZone = 0.06f;

    /// <summary>Cursor travel (pixels) for full deflection, for a viewport this tall.</summary>
    public static float RadiusPx(float viewportHeight) => MathF.Max(60f, viewportHeight * 0.18f);

    /// <summary>Stick deflection for a cursor at <paramref name="cursor"/> when the button
    /// went down at <paramref name="origin"/> (window pixels, Y grows downwards).
    /// X is yaw and Y is pitch in <see cref="ShipInput"/> terms (+X = turn right,
    /// +Y = nose up); the vector's length never exceeds 1. The response is softer near
    /// the centre for fine aiming and reaches full deflection at the radius.</summary>
    public static Vector2 Deflection(Vector2 origin, Vector2 cursor, float radiusPx)
    {
        var v = (cursor - origin) / MathF.Max(radiusPx, 1f);
        v.Y = -v.Y;
        float len = v.Length;
        if (len <= DeadZone) return Vector2.Zero;
        float m = MathF.Min(1f, (len - DeadZone) / (1f - DeadZone));
        m *= 0.4f + 0.6f * m;
        return v / len * m;
    }
}

/// <summary>Fixed geometry of the ship that several systems agree on (local units).</summary>
public static class ShipLayout
{
    /// <summary>World units per local unit, i.e. the ship's length in the world.</summary>
    public const float Length = 0.05f;
    /// <summary>Centres of the two engine exhaust exits (local space).</summary>
    public static readonly Vector3[] EngineNozzles =
    [
        new(-0.17f, 0.02f, 0.47f),
        new( 0.17f, 0.02f, 0.47f),
    ];
    /// <summary>Radius of each engine exit (local units).</summary>
    public const float NozzleRadius = 0.065f;
    /// <summary>Wing tips, for navigation lights and tip trails (local space).</summary>
    public static readonly Vector3[] WingTips =
    [
        new(-0.46f, -0.01f, 0.30f),
        new( 0.46f, -0.01f, 0.30f),
    ];
    /// <summary>Rough bounding radius of the hull (local units).</summary>
    public const float BoundingRadius = 0.62f;
}

/// <summary>
/// Everything the flight-mode systems need for one frame. The window glue fills the
/// time and anchor fields, <see cref="ShipFlight.WriteTo"/> fills the ship fields;
/// renderers and effects only read it.
/// </summary>
public sealed class FlightContext
{
    // ---- Time ----------------------------------------------------------------------
    /// <summary>Seconds simulated this frame (0 while paused).</summary>
    public float Dt;
    /// <summary>Seconds of flight so far; stops while paused. Drive animation with it.</summary>
    public float Time;

    // ---- The body we are flying around ---------------------------------------------
    /// <summary>Centre of the anchor planet (world units). It moves a little every frame
    /// as the planet orbits, and everything flight-related moves with it.</summary>
    public Vector3 AnchorPosition;
    public float AnchorRadius;
    /// <summary>Average colour of the anchor planet (0..1), e.g. for bounce light.</summary>
    public Vector3 AnchorColor;
    public bool HasRing;
    /// <summary>Unit normal of the ring plane (the planet's spin axis).</summary>
    public Vector3 RingNormal = Vector3.UnitY;
    /// <summary>Inner / outer radius of the ring band, world units from the anchor centre.</summary>
    public float RingInner, RingOuter;
    /// <summary>The Sun: the only light source (world units).</summary>
    public Vector3 SunPosition;

    // ---- The ship ------------------------------------------------------------------
    /// <summary>Ship centre (world units).</summary>
    public Vector3 ShipPosition;
    /// <summary>Ship local → world rotation.</summary>
    public Quaternion ShipOrientation = Quaternion.Identity;
    /// <summary>World-space unit axes of the ship.</summary>
    public Vector3 ShipForward = -Vector3.UnitZ, ShipUp = Vector3.UnitY, ShipRight = Vector3.UnitX;
    /// <summary>Velocity relative to the anchor planet (world units per second).</summary>
    public Vector3 ShipVelocity;
    /// <summary>Length of <see cref="ShipVelocity"/>.</summary>
    public float Speed;
    /// <summary>Speed as a fraction of the top (boost) speed, 0 .. 1.</summary>
    public float Speed01;
    /// <summary>Engine output 0 .. 1: about 0.5 when cruising, 1 at full boost, lower when braking.</summary>
    public float Throttle01;
    /// <summary>Smoothed boost amount 0 .. 1 (rises and falls over a fraction of a second).</summary>
    public float Boost01;
    /// <summary>Boost reserve 0 .. 1: drains while boosting, refills slowly and a little
    /// with every race gate. At 0 the boost cuts out.</summary>
    public float BoostEnergy01 = 1f;
    /// <summary>Ship local → world transform (scale * rotation * translation).</summary>
    public Matrix4 ShipModel = Matrix4.Identity;

    /// <summary>A point given in ship local space, in world units.</summary>
    public Vector3 ShipPoint(Vector3 local)
        => ShipPosition + Vector3.Transform(local * ShipLayout.Length, ShipOrientation);
}

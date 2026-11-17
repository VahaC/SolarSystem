// PROTOTYPE (2026-11-17): arcade flight mode — flight model and chase camera.
using OpenTK.Mathematics;

namespace SolarSystem;

// Conventions (verified by SolarSystem.Tests/ShipFlightTests.cs):
//  * Ship local axes: forward = -Z, up = +Y, right = +X.
//  * Vector3.Transform(v, q) rotates v by q; (a * b) applies b first, then a. So
//    q * Local(...) turns the ship about its own axes, World(...) * q about world axes.
//  * Quaternion.FromAxisAngle is right-handed: +angle about the local +Y axis turns the
//    nose to the LEFT, +angle about +X raises the nose, +angle about -Z (the nose axis)
//    rolls clockwise seen from behind.
//
// Everything is integrated in fixed sub-steps of at most 1/240 s so the result does not
// depend on the frame rate (a 60 fps frame is exactly four 240 fps steps).

/// <summary>The ship's motion: turns <see cref="ShipInput"/> into position and attitude.
/// Arcade model: the ship always flies forward, inputs drive eased angular rates, yaw banks
/// the hull into the turn, velocity trails the nose slightly, and the anchor planet is a
/// solid sphere the ship slides along.</summary>
public sealed class ShipFlight
{
    /// <summary>Ship centre relative to the anchor planet's centre (world units, world axes).</summary>
    public Vector3 LocalPosition;
    /// <summary>Ship local → world rotation, including the visual bank.</summary>
    public Quaternion Orientation = Quaternion.Identity;
    /// <summary>World units per second, relative to the anchor planet.</summary>
    public Vector3 Velocity;
    /// <summary>Engine output 0 .. 1 for the effects: ≈ 0.5 cruising, 1 at full boost, low when braking.</summary>
    public float Throttle01 = 0.5f;
    /// <summary>Smoothed boost amount 0 .. 1 (rises in ≈ 0.2 s, falls in ≈ 0.55 s).</summary>
    public float Boost01;

    /// <summary>Speed with no boost or brake (world units per second, ≈ 7 ship lengths/s).</summary>
    public const float CruiseSpeed = 0.35f;
    /// <summary>Top speed at full boost (world units per second).</summary>
    public const float BoostSpeed = 1.4f;
    /// <summary>Speed the brake settles at.</summary>
    public const float BrakeSpeed = 0.12f;

    /// <summary>Full-input yaw rate (radians per second).</summary>
    public const float MaxYawRate = 1.3f;
    /// <summary>Full-input pitch rate (radians per second).</summary>
    public const float MaxPitchRate = 1.15f;
    /// <summary>Full-input roll rate (radians per second): a barrel roll takes ≈ 1.7 s.</summary>
    public const float MaxRollRate = 3.8f;
    /// <summary>Bank angle at the full yaw rate (degrees).</summary>
    public const float MaxBankDeg = 48f;
    /// <summary>The ship never gets closer to the anchor centre than its radius plus this.</summary>
    public const float ShellMargin = 0.1f;

    private const float MaxStep = 1f / 240f;
    private const float MaxFrameDt = 0.1f;
    private const float RateStiffness = 9f;     // angular-rate spring (critically damped)
    private const float RollStiffness = 11f;
    private const float BankStiffness = 6.5f;
    private const float SlideRate = 7f;         // how fast velocity catches the nose (1/s)
    private const float BoostRiseTime = 0.2f;
    private const float BoostFallTime = 0.55f;
    private const float SpeedUpRate = 3.2f;     // exponential approach rates (1/s)
    private const float SlowDownRate = 1.4f;
    private const float BrakeRate = 2.6f;
    private const float AvoidZone = 0.7f;       // planet steering starts this far above the shell
    private const float AvoidTurnRate = 3.5f;

    private Vector3 _rate, _rateVel;  // x = pitch (+ up), y = yaw (+ right), z = roll (+ right); rad/s
    private float _bank, _bankVel;    // radians, + = right wing down
    private float _boostRamp;         // linear 0..1, Boost01 is its smoothstep
    private float _speed = CruiseSpeed;
    private Vector3 _velDir = -Vector3.UnitZ;

    /// <summary>Unit nose direction (world).</summary>
    public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Orientation);
    /// <summary>Unit dorsal direction (world), banked.</summary>
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Orientation);
    /// <summary>Unit starboard direction (world), banked.</summary>
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Orientation);
    /// <summary>Current speed (world units per second).</summary>
    public float Speed => _speed;
    /// <summary>Current turn bank (radians, + = right wing down).</summary>
    public float BankRad => _bank;

    /// <summary>Place the ship at <paramref name="localPosition"/> (relative to the anchor)
    /// looking along <paramref name="forward"/> with <paramref name="up"/> roughly up.
    /// Clears all motion state (rates, bank, boost) and starts at cruise speed.</summary>
    public void Reset(Vector3 localPosition, Vector3 forward, Vector3 up)
    {
        LocalPosition = localPosition;
        var f = forward.Normalized();
        var r = Vector3.Cross(f, up);
        if (r.LengthSquared < 1e-8f) r = Vector3.Cross(f, MathF.Abs(f.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        r.Normalize();
        var u = Vector3.Cross(r, f);
        // Quaternion.FromMatrix expects the images of local X, Y, Z as COLUMNS (the
        // placeholder passed them as rows, which gave the inverse rotation).
        Orientation = Quaternion.FromMatrix(Matrix3.Transpose(new Matrix3(r, u, -f))).Normalized();
        _rate = _rateVel = Vector3.Zero;
        _bank = _bankVel = 0f;
        _boostRamp = 0f;
        Boost01 = 0f;
        _speed = CruiseSpeed;
        _velDir = f;
        Velocity = f * CruiseSpeed;
        Throttle01 = 0.5f;
    }

    /// <summary>Advance by <paramref name="dt"/> seconds. The anchor fields of
    /// <paramref name="ctx"/> (radius) are already filled for this frame. <c>dt &lt;= 0</c>
    /// changes nothing; frames longer than 0.1 s are clamped.</summary>
    public void Update(float dt, in ShipInput input, FlightContext ctx)
    {
        if (!(dt > 0f)) return;
        dt = MathF.Min(dt, MaxFrameDt);
        int steps = Math.Max(1, (int)MathF.Ceiling(dt / MaxStep - 1e-3f));
        float h = dt / steps;

        // The flight frame is the attitude without the cosmetic bank.
        var frame = (Orientation * Inverse(BankQ(_bank))).Normalized();
        float pitchIn = Math.Clamp(input.Pitch, -1f, 1f);
        float yawIn = Math.Clamp(input.Yaw, -1f, 1f);
        float rollIn = Math.Clamp(input.Roll, -1f, 1f);

        for (int i = 0; i < steps; i++)
            Step(h, pitchIn, yawIn, rollIn, input.Boost, input.Brake, ctx.AnchorRadius, ref frame);

        Orientation = (frame * BankQ(_bank)).Normalized();
        Velocity = _velDir * _speed;
    }

    private void Step(float h, float pitchIn, float yawIn, float rollIn, bool boost, bool brake,
                      float anchorRadius, ref Quaternion frame)
    {
        // ---- Boost / speed / throttle ----
        _boostRamp = Math.Clamp(_boostRamp + (boost && !brake ? h / BoostRiseTime : -h / BoostFallTime), 0f, 1f);
        Boost01 = _boostRamp * _boostRamp * (3f - 2f * _boostRamp);
        float target = brake ? BrakeSpeed : CruiseSpeed + (BoostSpeed - CruiseSpeed) * Boost01;
        float k = target > _speed ? SpeedUpRate : brake ? BrakeRate : SlowDownRate;
        _speed += (target - _speed) * (1f - MathF.Exp(-k * h));

        float accel01 = Math.Clamp((target - _speed) / (BoostSpeed - CruiseSpeed), 0f, 1f);
        float throttleTarget = brake ? 0.12f : Math.Clamp(0.5f + 0.5f * Boost01 + 0.25f * accel01, 0f, 1f);
        Throttle01 += (throttleTarget - Throttle01) * (1f - MathF.Exp(-10f * h));

        // ---- Angular rates: critically damped springs (ease in and out, no overshoot) ----
        float agility = 1f - 0.2f * Boost01 + (brake ? 0.15f : 0f);
        var rateTarget = new Vector3(pitchIn * MaxPitchRate * agility, yawIn * MaxYawRate * agility, rollIn * MaxRollRate);
        SpringStep(ref _rate.X, ref _rateVel.X, rateTarget.X, RateStiffness, h);
        SpringStep(ref _rate.Y, ref _rateVel.Y, rateTarget.Y, RateStiffness, h);
        SpringStep(ref _rate.Z, ref _rateVel.Z, rateTarget.Z, RollStiffness, h);

        float bankTarget = MathHelper.DegreesToRadians(MaxBankDeg) * Math.Clamp(_rate.Y / MaxYawRate, -1f, 1f);
        SpringStep(ref _bank, ref _bankVel, bankTarget, BankStiffness, h);

        // Body-axis rotation of the flight frame (see the conventions at the top).
        frame = frame
                * Quaternion.FromAxisAngle(Vector3.UnitX, _rate.X * h)
                * Quaternion.FromAxisAngle(Vector3.UnitY, -_rate.Y * h)
                * Quaternion.FromAxisAngle(-Vector3.UnitZ, _rate.Z * h);
        frame.Normalize();

        var fwd = Vector3.Transform(-Vector3.UnitZ, frame);

        // ---- Anchor planet: steer the nose off it when close, then a hard shell ----
        float shell = anchorRadius + ShellMargin;
        if (anchorRadius > 0f)
        {
            float r = LocalPosition.Length;
            if (r > 1e-6f)
            {
                var n = LocalPosition / r;
                float inward = -Vector3.Dot(fwd, n);
                float prox = Math.Clamp(1f - (r - shell) / AvoidZone, 0f, 1f);
                if (inward > 0f && prox > 0f)
                {
                    var axis = Vector3.Cross(fwd, n);   // rotating fwd about this moves it toward n
                    if (axis.LengthSquared < 1e-8f) axis = Vector3.Transform(Vector3.UnitX, frame);
                    axis.Normalize();
                    float angle = AvoidTurnRate * prox * prox * inward * h;
                    frame = (Quaternion.FromAxisAngle(axis, angle) * frame).Normalized();
                    fwd = Vector3.Transform(-Vector3.UnitZ, frame);
                }
            }
        }

        // ---- Velocity trails the nose a little (weighty but precise) ----
        float a = 1f - MathF.Exp(-SlideRate * h);
        var vd = _velDir + (fwd - _velDir) * a;
        _velDir = vd.LengthSquared > 1e-8f ? vd.Normalized() : fwd;

        LocalPosition += _velDir * (_speed * h);

        if (anchorRadius > 0f)
        {
            float r = LocalPosition.Length;
            if (r < shell)
            {
                var n = r > 1e-6f ? LocalPosition / r : Vector3.Transform(Vector3.UnitY, frame);
                LocalPosition = n * shell;
                float vin = Vector3.Dot(_velDir, n);
                if (vin < 0f)
                {
                    var slide = _velDir - vin * n;
                    if (slide.LengthSquared < 1e-6f)
                    {
                        slide = fwd - Vector3.Dot(fwd, n) * n;
                        if (slide.LengthSquared < 1e-6f) slide = Vector3.Transform(Vector3.UnitY, frame) - Vector3.Dot(Vector3.Transform(Vector3.UnitY, frame), n) * n;
                    }
                    _velDir = slide.Normalized();
                }
            }
        }
    }

    /// <summary>Publish the ship's state into the shared frame context.</summary>
    public void WriteTo(FlightContext ctx)
    {
        ctx.ShipPosition = ctx.AnchorPosition + LocalPosition;
        ctx.ShipOrientation = Orientation;
        ctx.ShipForward = Forward;
        ctx.ShipUp = Up;
        ctx.ShipRight = Right;
        ctx.ShipVelocity = Velocity;
        ctx.Speed = Velocity.Length;
        ctx.Speed01 = Math.Clamp(ctx.Speed / BoostSpeed, 0f, 1f);
        ctx.Throttle01 = Math.Clamp(Throttle01, 0f, 1f);
        ctx.Boost01 = Math.Clamp(Boost01, 0f, 1f);
        ctx.ShipModel = Matrix4.CreateScale(ShipLayout.Length)
                        * Matrix4.CreateFromQuaternion(Orientation)
                        * Matrix4.CreateTranslation(ctx.ShipPosition);
    }

    private static Quaternion BankQ(float bank) => Quaternion.FromAxisAngle(-Vector3.UnitZ, bank);

    private static Quaternion Inverse(Quaternion q) => new(-q.Xyz, q.W);

    /// <summary>One semi-implicit step of a critically damped spring (no overshoot).</summary>
    internal static void SpringStep(ref float x, ref float v, float target, float omega, float h)
    {
        v += (omega * omega * (target - x) - 2f * omega * v) * h;
        x += v * h;
    }
}

/// <summary>Third-person camera that follows the ship: spring-damped, behind and above,
/// looking ahead; lags the ship's turns and roll so the ship visibly moves and banks on
/// screen; pulls back, widens the field of view and shakes a little on boost.</summary>
public sealed class ChaseCamera
{
    /// <summary>Eye position (world).</summary>
    public Vector3 Eye;
    /// <summary>Point the camera looks at (world).</summary>
    public Vector3 LookAt;
    /// <summary>Camera up (world, unit, orthogonal to the view direction).</summary>
    public Vector3 UpVector = Vector3.UnitY;
    /// <summary>Vertical field of view (degrees).</summary>
    public float FovDeg = BaseFovDeg;

    /// <summary>Field of view at cruise (degrees).</summary>
    public const float BaseFovDeg = 60f;
    /// <summary>Field of view at full boost (degrees).</summary>
    public const float BoostFovDeg = 67f;
    /// <summary>Distance behind the ship at cruise (world units, 2.8 ship lengths).</summary>
    public const float BackDistance = 2.8f * ShipLayout.Length;
    /// <summary>Extra distance behind the ship at full boost.</summary>
    public const float BoostBackExtra = 0.22f * ShipLayout.Length;
    /// <summary>Eye height above the ship's flight line.</summary>
    public const float Height = 0.72f * ShipLayout.Length;
    /// <summary>How far ahead of the ship the camera looks.</summary>
    public const float LookAhead = 3.2f * ShipLayout.Length;
    /// <summary>Height of the look-at point above the flight line (puts the ship below centre).</summary>
    public const float LookHeight = 0.12f * ShipLayout.Length;

    private const float MaxStep = 1f / 240f;
    private const float MaxFrameDt = 0.1f;
    private const float FollowRate = 4.5f;      // direction lag (1/s)
    private const float RollFollowRate = 2.6f;  // roll lag (1/s)
    private const float OffsetStiffness = 9f;   // positional spring
    private const float BankCancel = 0.65f;     // share of the ship's turn bank the camera follows statically
    private const float SurgeGain = 0.02f;      // extra pull-back per unit/s of speed the camera has not caught up with
    private const float ShakeAmp = 0.035f * ShipLayout.Length;
    private const float EyeClearance = 0.03f;

    private Vector3 _fwd = -Vector3.UnitZ, _up = Vector3.UnitY;
    private Vector3 _offset, _offsetVel;
    private Vector3 _prevShipFwd = -Vector3.UnitZ;
    private float _speedLag, _bankEst;
    private bool _initialized;

    /// <summary>Snap directly behind the ship (no lag) — call when flight mode starts.</summary>
    public void Reset(FlightContext ctx)
    {
        _fwd = ctx.ShipForward.Normalized();
        _up = Orthogonalize(ctx.ShipUp, _fwd);
        _speedLag = ctx.Speed;
        _bankEst = 0f;
        _prevShipFwd = _fwd;
        _offset = DesiredOffset(ctx);
        _offsetVel = Vector3.Zero;
        FovDeg = BaseFovDeg + (BoostFovDeg - BaseFovDeg) * ctx.Boost01;
        _initialized = true;
        Compose(ctx);
    }

    /// <summary>Follow the ship for this frame (uses <see cref="FlightContext.Dt"/>;
    /// <c>Dt == 0</c> keeps the pose and only re-derives it from the current ship state).</summary>
    public void Update(FlightContext ctx)
    {
        if (!_initialized) { Reset(ctx); return; }
        float dt = ctx.Dt;
        if (dt > 0f)
        {
            dt = MathF.Min(dt, MaxFrameDt);
            var shipFwd = ctx.ShipForward.Normalized();

            // Estimate the ship's turn bank from how fast its nose swings about its up axis,
            // so the camera can keep part of the bank visible instead of rolling with it.
            // Cross(fwd, right) = -up, so a right turn gives a negative dot; flip it.
            float turnRate = -Vector3.Dot(Vector3.Cross(_prevShipFwd, shipFwd), ctx.ShipUp) / dt;
            float bankTarget = MathHelper.DegreesToRadians(ShipFlight.MaxBankDeg)
                               * Math.Clamp(turnRate / ShipFlight.MaxYawRate, -1f, 1f);
            _bankEst += (bankTarget - _bankEst) * (1f - MathF.Exp(-10f * dt));
            _prevShipFwd = shipFwd;

            var upTarget = RotateAbout(ctx.ShipUp, shipFwd, -_bankEst * BankCancel);

            int steps = Math.Max(1, (int)MathF.Ceiling(dt / MaxStep - 1e-3f));
            float h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                _fwd = NLerp(_fwd, shipFwd, 1f - MathF.Exp(-FollowRate * h));
                _up = Orthogonalize(NLerp(_up, upTarget, 1f - MathF.Exp(-RollFollowRate * h)), _fwd);
                _speedLag += (ctx.Speed - _speedLag) * (1f - MathF.Exp(-2.5f * h));
                var desired = DesiredOffset(ctx);
                _offsetVel += (OffsetStiffness * OffsetStiffness * (desired - _offset) - 2f * OffsetStiffness * _offsetVel) * h;
                _offset += _offsetVel * h;
            }

            float fovTarget = BaseFovDeg + (BoostFovDeg - BaseFovDeg) * ctx.Boost01;
            FovDeg += (fovTarget - FovDeg) * (1f - MathF.Exp(-6f * dt));
        }
        Compose(ctx);
    }

    private Vector3 DesiredOffset(FlightContext ctx)
    {
        float surge = Math.Clamp(ctx.Speed - _speedLag, -0.3f, 1.2f) * SurgeGain;
        float dist = BackDistance + BoostBackExtra * ctx.Boost01 + surge;
        return -_fwd * dist + _up * (Height * (1f + 0.06f * ctx.Boost01));
    }

    private void Compose(FlightContext ctx)
    {
        var ship = ctx.ShipPosition;
        var right = Vector3.Cross(_fwd, _up).Normalized();

        // Small deterministic boost shake (sum of incommensurate sines of flight time).
        float t = ctx.Time;
        float amp = ShakeAmp * ctx.Boost01 * (0.5f + 0.5f * ctx.Speed01);
        float sx = MathF.Sin(t * 37.1f) * 0.6f + MathF.Sin(t * 61.7f + 1.3f) * 0.4f;
        float sy = MathF.Sin(t * 43.9f + 0.7f) * 0.6f + MathF.Sin(t * 71.3f + 2.1f) * 0.4f;
        var shake = (right * sx + _up * sy) * amp;

        Eye = ship + _offset + shake;
        var aheadDir = (_fwd + ctx.ShipForward).Normalized();
        if (!float.IsFinite(aheadDir.X)) aheadDir = _fwd;
        LookAt = ship + aheadDir * LookAhead + _up * LookHeight + shake * 0.4f;

        // Never inside the anchor planet.
        if (ctx.AnchorRadius > 0f)
        {
            var rel = Eye - ctx.AnchorPosition;
            float min = ctx.AnchorRadius + EyeClearance;
            float r = rel.Length;
            if (r < min)
                Eye = ctx.AnchorPosition + (r > 1e-6f ? rel / r : _up) * min;
        }

        var view = LookAt - Eye;
        UpVector = view.LengthSquared > 1e-12f ? Orthogonalize(_up, view.Normalized()) : _up;
    }

    /// <summary>Hand the pose to the scene camera every renderer reads.</summary>
    public void ApplyTo(Camera cam)
    {
        cam.EyeOverride = Eye;
        cam.ViewOverride = Matrix4.LookAt(Eye, LookAt, UpVector);
        cam.Target = LookAt;
        cam.Distance = MathF.Max((Eye - LookAt).Length, 1e-4f);
        cam.FovDeg = FovDeg;
    }

    private static Vector3 NLerp(Vector3 a, Vector3 b, float t)
    {
        var v = a + (b - a) * t;
        return v.LengthSquared > 1e-10f ? v.Normalized() : b;
    }

    private static Vector3 Orthogonalize(Vector3 v, Vector3 axis)
    {
        var o = v - Vector3.Dot(v, axis) * axis;
        if (o.LengthSquared < 1e-10f)
            o = Vector3.Cross(axis, MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
        return o.Normalized();
    }

    private static Vector3 RotateAbout(Vector3 v, Vector3 axis, float angle)
        => Vector3.Transform(v, Quaternion.FromAxisAngle(axis, angle));
}

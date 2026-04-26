using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SolarSystem;

/// <summary>Arcball / orbit camera around a target.</summary>
public sealed class Camera
{
    public Vector3 Target = Vector3.Zero;
    public float Distance = 60f;
    public float Yaw = MathHelper.DegreesToRadians(35f);
    public float Pitch = MathHelper.DegreesToRadians(35f);
    public float FovDeg = 55f;
    public float Aspect = 16f / 9f;
    /// <summary>Smallest distance the user can zoom to (mouse-wheel clamp). Lowered for
    /// real-scale mode where bodies are sub-unit in size.</summary>
    public float MinDistance = 2f;
    public float MaxDistance = 4000f;
    public float Far = 8000f;

    /// <summary>Near plane is scaled with the current orbit radius so we keep a roughly
    /// constant near/far ratio across many orders of magnitude of zoom. Without this, a
    /// fixed 0.1 near plane clips planets long before the camera can get close enough
    /// to see them in real-scale mode.</summary>
    public float Near => MathF.Max(0.001f, Distance * 0.002f);

    private const float DefaultDistance = 320f;
    private const float DefaultYaw = 0.6f;
    private const float DefaultPitch = 1.0f;

    public void ResetDefault()
    {
        Yaw = DefaultYaw;
        Pitch = DefaultPitch;
        Distance = DefaultDistance;
    }

    public Vector3 Eye
    {
        get
        {
            float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
            float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);
            var dir = new Vector3(cp * sy, sp, cp * cy);
            return Target + dir * Distance;
        }
    }

    public Matrix4 ViewMatrix => Matrix4.LookAt(Eye, Target, Vector3.UnitY);
    public Matrix4 ProjectionMatrix =>
        Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(FovDeg), Aspect, Near, Far);

    private bool _rightDown, _middleDown;
    private Vector2 _lastMouse;

    public void HandleMouseDown(MouseButtonEventArgs e, Vector2 pos)
    {
        if (e.Button == MouseButton.Right) _rightDown = true;
        if (e.Button == MouseButton.Middle) _middleDown = true;
        _lastMouse = pos;
    }

    public void HandleMouseUp(MouseButtonEventArgs e)
    {
        if (e.Button == MouseButton.Right) _rightDown = false;
        if (e.Button == MouseButton.Middle) _middleDown = false;
    }

    public void HandleMouseMove(Vector2 pos)
    {
        var d = pos - _lastMouse;
        _lastMouse = pos;
        if (_rightDown)
        {
            Yaw -= d.X * 0.005f;
            Pitch += d.Y * 0.005f;
            Pitch = Math.Clamp(Pitch, -1.55f, 1.55f);
        }
        else if (_middleDown)
        {
            // Pan in the camera's local plane.
            var view = ViewMatrix;
            var right = new Vector3(view.M11, view.M21, view.M31);
            var up = new Vector3(view.M12, view.M22, view.M32);
            float scale = Distance * 0.0015f;
            Target += -right * d.X * scale + up * d.Y * scale;
        }
    }

    public void HandleScroll(float dy)
    {
        Distance *= MathF.Pow(0.9f, dy);
        Distance = Math.Clamp(Distance, MinDistance, MaxDistance);
    }
}

using OpenTK.Graphics.OpenGL4;

namespace SolarSystem;

/// <summary>
/// A8: minimal wrapper around a single-stage compute shader program. Mirrors
/// the public surface of <see cref="ShaderProgram"/> for the uniform setters
/// we actually need from the asteroid-belt GPU Kepler solve.
/// </summary>
public sealed class ComputeProgram : IDisposable
{
    public int Handle { get; private set; }
    private readonly Dictionary<string, int> _uniforms = new();

    public ComputeProgram(string source)
    {
        int cs = GL.CreateShader(ShaderType.ComputeShader);
        GL.ShaderSource(cs, source);
        GL.CompileShader(cs);
        GL.GetShader(cs, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = GL.GetShaderInfoLog(cs);
            GL.DeleteShader(cs);
            throw new Exception("Compute shader compile error: " + log);
        }
        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, cs);
        GL.LinkProgram(Handle);
        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = GL.GetProgramInfoLog(Handle);
            GL.DeleteProgram(Handle);
            GL.DeleteShader(cs);
            Handle = 0;
            throw new Exception("Compute program link error: " + log);
        }
        GL.DetachShader(Handle, cs);
        GL.DeleteShader(cs);
    }

    public void Use() => GL.UseProgram(Handle);

    private int Loc(string name)
    {
        if (!_uniforms.TryGetValue(name, out int l))
        {
            l = GL.GetUniformLocation(Handle, name);
            _uniforms[name] = l;
        }
        return l;
    }

    public void SetFloat(string name, float v) => GL.Uniform1(Loc(name), v);
    public void SetInt(string name, int v)     => GL.Uniform1(Loc(name), v);

    public void Dispose()
    {
        if (Handle != 0) GL.DeleteProgram(Handle);
        Handle = 0;
    }
}

using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

public sealed class ShaderProgram : IDisposable
{
    public int Handle { get; }
    private readonly Dictionary<string, int> _uniforms = new();

    public ShaderProgram(string vertexSrc, string fragmentSrc)
    {
        int vs = Compile(ShaderType.VertexShader, vertexSrc);
        int fs = Compile(ShaderType.FragmentShader, fragmentSrc);
        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, vs);
        GL.AttachShader(Handle, fs);
        GL.LinkProgram(Handle);
        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0)
            throw new Exception("Program link error: " + GL.GetProgramInfoLog(Handle));
        GL.DetachShader(Handle, vs);
        GL.DetachShader(Handle, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
    }

    private static int Compile(ShaderType type, string src)
    {
        int s = GL.CreateShader(type);
        GL.ShaderSource(s, src);
        GL.CompileShader(s);
        GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            throw new Exception($"{type} compile error: " + GL.GetShaderInfoLog(s));
        return s;
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

    public void SetMatrix4(string name, Matrix4 m) { var mm = m; GL.UniformMatrix4(Loc(name), false, ref mm); }
    public void SetVector3(string name, Vector3 v) => GL.Uniform3(Loc(name), v);
    public void SetVector4(string name, Vector4 v) => GL.Uniform4(Loc(name), v);
    public void SetVector2(string name, Vector2 v) => GL.Uniform2(Loc(name), v);
    public void SetFloat(string name, float f) => GL.Uniform1(Loc(name), f);
    public void SetInt(string name, int i) => GL.Uniform1(Loc(name), i);

    public void Dispose() => GL.DeleteProgram(Handle);
}

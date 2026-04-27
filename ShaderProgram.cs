using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

public sealed class ShaderProgram : IDisposable
{
    public int Handle { get; private set; }
    private readonly Dictionary<string, int> _uniforms = new();

    public ShaderProgram(string vertexSrc, string fragmentSrc)
    {
        Handle = LinkProgram(vertexSrc, fragmentSrc);
    }

    /// <summary>A6: Recompile this program from new GLSL sources and swap the GL
    /// handle in place. Throws (without modifying state) if the new program fails
    /// to compile or link, so a typo in a watched .glsl file simply leaves the old
    /// program running.</summary>
    public void Reload(string vertexSrc, string fragmentSrc)
    {
        int newHandle = LinkProgram(vertexSrc, fragmentSrc);
        int old = Handle;
        Handle = newHandle;
        _uniforms.Clear();
        if (old != 0) GL.DeleteProgram(old);
    }

    private static int LinkProgram(string vertexSrc, string fragmentSrc)
    {
        int vs = Compile(ShaderType.VertexShader, vertexSrc);
        int fs = Compile(ShaderType.FragmentShader, fragmentSrc);
        int handle = GL.CreateProgram();
        GL.AttachShader(handle, vs);
        GL.AttachShader(handle, fs);
        GL.LinkProgram(handle);
        GL.GetProgram(handle, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0)
        {
            string log = GL.GetProgramInfoLog(handle);
            GL.DeleteProgram(handle);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
            throw new Exception("Program link error: " + log);
        }
        GL.DetachShader(handle, vs);
        GL.DetachShader(handle, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        return handle;
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

    /// <summary>Uploads <paramref name="count"/> vec4 elements (so <paramref name="data"/>
    /// must hold at least count*4 floats) to a `uniform vec4 name[N]` array.</summary>
    public void SetVector4Array(string name, float[] data, int count)
    {
        if (count <= 0) return;
        GL.Uniform4(Loc(name), count, data);
    }

    public void Dispose() => GL.DeleteProgram(Handle);
}

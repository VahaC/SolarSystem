#version 330 core
layout(location=0) in vec2 aPos; layout(location=1) in vec2 aUv;
uniform mat4 uView; uniform mat4 uProj; uniform vec3 uCenter; uniform float uSize;
uniform float uFcoef;
out vec2 vUv;
void main(){
    // Billboard: place center, then offset by quad in view space.
    vec4 cs = uView * vec4(uCenter, 1.0);
    cs.xy += aPos * uSize;
    gl_Position = uProj * cs;
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
    vUv = aPos; // -1..1
}

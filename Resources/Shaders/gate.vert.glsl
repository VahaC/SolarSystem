#version 330 core
// PROTOTYPE (2026-11-17): arcade flight mode — gate race: gate visuals vertex shader.
// All geometry is built on the CPU in world space RELATIVE TO THE CAMERA EYE (precision
// far from the Sun); uView is the view matrix with the eye translation folded in.
layout(location=0) in vec3 aPos;   // world position minus camera eye
layout(location=1) in vec4 aUv;    // xy = per-mode parametrisation, zw = per-mode parameters
layout(location=2) in vec4 aCol;   // rgb = HDR tint, a = intensity
layout(location=3) in float aMode; // effect kind (see gate.frag.glsl)

uniform mat4 uView; uniform mat4 uProj;
uniform float uFcoef;

out vec4 vUv;
out vec4 vCol;
flat out int vMode;

void main(){
    gl_Position = uProj * uView * vec4(aPos, 1.0);
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
    vUv = aUv;
    vCol = aCol;
    vMode = int(aMode + 0.5);
}

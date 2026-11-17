#version 330 core
// PROTOTYPE (2026-11-17): arcade flight mode — ship effects (plumes, glows, trails, speed
// streaks). All geometry is built on the CPU in world space RELATIVE TO THE CAMERA EYE
// (better float precision far from the Sun); uView is the view matrix with the eye
// translation folded in.
layout(location=0) in vec3 aPos;   // world position minus camera eye
layout(location=1) in vec2 aUv;    // per-mode parametrisation (see shipfx.frag.glsl)
layout(location=2) in vec4 aCol;   // rgb = HDR tint, a = intensity
layout(location=3) in float aMode; // effect kind

uniform mat4 uView; uniform mat4 uProj;
uniform float uFcoef;

out vec2 vUv;
out vec4 vCol;
flat out int vMode;

void main(){
    gl_Position = uProj * uView * vec4(aPos, 1.0);
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
    vUv = aUv;
    vCol = aCol;
    vMode = int(aMode + 0.5);
}

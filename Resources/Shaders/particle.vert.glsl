#version 330 core
// A4: instanced billboard quad for CPU particle systems. The base quad is 4 verts
// with corners in [-1,1]; per-instance attributes carry the world-space position
// and remaining-life fraction.
layout(location=0) in vec2 aCorner;       // -1..1 quad corner
layout(location=1) in vec3 iPos;          // per-instance world position
layout(location=2) in float iLife;        // per-instance life01 (0=dead,1=newborn)

uniform mat4 uView; uniform mat4 uProj;
uniform vec2 uViewportSize;
uniform float uFcoef;

// Pixel-radius = clamp(uPxBase / dist, uPxMin, uPxMax) * mix(uLifeLo, uLifeHi, iLife).
// Setting uLifeLo == uLifeHi == 1.0 disables the life-driven scaling (e.g. asteroids).
uniform float uPxBase;
uniform float uPxMin;
uniform float uPxMax;
uniform float uLifeLo;
uniform float uLifeHi;

out vec2 vCorner;
out float vLife;

void main(){
    vec4 vp = uView * vec4(iPos, 1.0);
    float dist = max(1.0, -vp.z);
    float pxRadius = clamp(uPxBase / dist, uPxMin, uPxMax) * mix(uLifeLo, uLifeHi, iLife);

    vec4 clip = uProj * vp;
    // Convert pixel offset to clip-space: NDC offset = (px / viewport) * 2,
    // then multiply by clip.w so it survives the perspective divide.
    vec2 ndcOff = (aCorner * pxRadius * 2.0) / uViewportSize;
    clip.xy += ndcOff * clip.w;
    clip.z = (log2(max(1e-6, 1.0 + clip.w)) * uFcoef - 1.0) * clip.w;
    gl_Position = clip;

    vCorner = aCorner;
    vLife = iLife;
}

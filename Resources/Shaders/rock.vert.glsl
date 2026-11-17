#version 330 core
// PROTOTYPE (2026-11-17): arcade flight mode — instanced ring rocks. Every instance is
// placed procedurally: a fixed offset inside a square tile of side uPeriod (ring-plane
// coordinates relative to the anchor planet), wrapped to the copy nearest the camera.
// Size, height, existence, spin and colour come from a hash of (instance, tile), so the
// field never visibly repeats. RockField.cs mirrors this exactly for collisions — keep
// the two in sync.
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;   // flat face normal
layout(location=2) in vec3 aSmooth;   // smoothed normal (crease-aware), used by big rocks

uniform mat4 uProj;
uniform mat4 uViewRot;      // view matrix without translation (camera-relative rendering)
uniform float uFcoef;
uniform vec3 uCamRing;      // eye in ring coordinates (u, h, w) relative to the anchor
uniform vec3 uBasisU, uBasisN, uBasisW;
uniform float uPeriod;
uniform uint uSeed;
uniform float uSizeMin, uSizeMax, uSizeGamma;
uniform float uDensity;
uniform float uThick;
uniform float uRingInner, uRingOuter;
uniform float uTime;
uniform float uPxPerRad;
uniform float uMinPx;
// Clear zones near the camera (ring coordinates relative to the anchor + radius) and
// their strength 0..1. RockField.SelectZones picks them; ClearFactor mirrors clearFactor.
uniform vec4 uZone[32];
uniform float uZoneK[32];
uniform int uZoneCount;

out vec3 vRel;              // camera-relative world position
out vec3 vNormal;
out vec3 vObj;              // object-space position (surface noise)
flat out vec3 vAlbedo;
flat out float vIce;
flat out float vSize;       // drawn radius (world units) — big rocks get extra detail

uint hash(uint x) {
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16; return x;
}
float rnd(uint h, uint n) { return float(hash(h + n * 0x68e31da4u) >> 8) * (1.0 / 16777216.0); }

float ringDensity(float r) {
    float t = (r - uRingInner) / (uRingOuter - uRingInner);
    if (t <= 0.0 || t >= 1.0) return 0.0;
    float d = smoothstep(0.0, 0.06, t) * (1.0 - smoothstep(0.93, 1.0, t));
    d *= mix(0.35, 1.0, smoothstep(0.22, 0.28, t));                        // faint C ring
    d *= mix(1.0, 0.04, smoothstep(0.62, 0.64, t) * (1.0 - smoothstep(0.69, 0.71, t))); // Cassini
    d *= mix(1.0, 0.75, smoothstep(0.69, 0.71, t));                        // A ring
    d *= 1.0 - (smoothstep(0.885, 0.893, t) * (1.0 - smoothstep(0.903, 0.911, t))); // Encke
    d *= 0.68 + 0.32 * sin(t * 157.0 + sin(t * 41.0) * 2.0);               // ringlets
    return d;
}

// 0..1 scale from the clear zones: rocks shrink smoothly to nothing as they approach a
// zone; the band (>= 1.3 x size) keeps the shrunken body outside the zone.
float clearFactor(vec3 c, float size, uint th) {
    float f = 1.0;
    float band = size * (1.3 + 0.6 * rnd(th, 14u)) + 0.03;
    for (int i = 0; i < uZoneCount; i++) {
        vec4 z = uZone[i];
        float t = clamp((length(c - z.xyz) - z.w) / band, 0.0, 1.0);
        f = min(f, mix(1.0, t * t * (3.0 - 2.0 * t), uZoneK[i]));
    }
    return f;
}

void main() {
    uint idh = hash(uSeed * 0x9e3779b9u + uint(gl_InstanceID));
    vec2 o = vec2(rnd(idh, 0u), rnd(idh, 1u)) * uPeriod;
    vec2 c = uCamRing.xz;
    vec2 k = floor((c - o) / uPeriod + 0.5);
    vec2 q = o + k * uPeriod;
    ivec2 ki = ivec2(k);
    uint th = hash(idh ^ hash(uint(ki.x) * 73856093u ^ uint(ki.y) * 19349663u));

    float exist = step(rnd(th, 0u), ringDensity(length(q)) * uDensity);
    float size = uSizeMin * pow(uSizeMax / uSizeMin, pow(rnd(th, 1u), uSizeGamma));
    float hs = rnd(th, 2u) < 0.85 ? 0.04 : 0.12;
    float hgt = (rnd(th, 3u) - 0.5) * hs * uThick;

    float R = 0.5 * uPeriod;
    float fade = 1.0 - smoothstep(0.72 * R, 0.97 * R, length(q - c));
    vec3 centerRel = uBasisU * (q.x - c.x) + uBasisN * (hgt - uCamRing.y) + uBasisW * (q.y - c.y);
    float dist = length(centerRel);
    float px = size * uPxPerRad / max(dist, 1e-4);
    float vis = exist * fade * smoothstep(uMinPx, uMinPx * 2.0, px);
    if (vis > 0.0) vis *= clearFactor(vec3(q.x, hgt, q.y), size, th);
    if (vis <= 0.0) {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);   // whole instance outside the clip volume
        vRel = vec3(0); vNormal = vec3(0, 1, 0); vObj = vec3(0); vAlbedo = vec3(0); vIce = 0.0; vSize = 0.0;
        return;
    }

    // Tumble: random axis, slower for big rocks.
    float z = rnd(th, 4u) * 2.0 - 1.0;
    float a = rnd(th, 5u) * 6.2831853;
    vec3 axis = vec3(sqrt(1.0 - z * z) * cos(a), z, sqrt(1.0 - z * z) * sin(a));
    float spin = mix(0.08, 0.7, rnd(th, 6u)) * clamp(0.02 / size, 0.15, 1.5);
    float ang = rnd(th, 7u) * 6.2831853 + uTime * spin;
    float s = sin(ang), co = cos(ang), ic = 1.0 - co;
    mat3 rot = mat3(
        co + axis.x * axis.x * ic,          axis.y * axis.x * ic + axis.z * s, axis.z * axis.x * ic - axis.y * s,
        axis.x * axis.y * ic - axis.z * s,  co + axis.y * axis.y * ic,         axis.z * axis.y * ic + axis.x * s,
        axis.x * axis.z * ic + axis.y * s,  axis.y * axis.z * ic - axis.x * s, co + axis.z * axis.z * ic);

    float scale = size * vis;
    vec3 rel = centerRel + rot * (aPos * scale);
    vRel = rel;
    // Big rocks: blend toward the crease-aware smooth normal so the rounded parts lose
    // their facets (cut planes stay crisp); small rocks keep the flat look.
    vNormal = rot * normalize(mix(aNormal, aSmooth, 0.8 * smoothstep(0.012, 0.045, size)));
    vSize = size;
    vObj = aPos * 3.0 + vec3(rnd(th, 8u), rnd(th, 9u), rnd(th, 10u)) * 17.0;

    // Palette: dirty ice whites, tans, greys.
    float pc = rnd(th, 11u);
    vec3 ice   = vec3(0.86, 0.87, 0.89);
    vec3 dirty = vec3(0.74, 0.70, 0.63);
    vec3 tan   = vec3(0.60, 0.50, 0.39);
    vec3 grey  = vec3(0.46, 0.45, 0.44);
    vec3 alb = pc < 0.45 ? mix(ice, dirty, rnd(th, 12u) * 0.6)
             : pc < 0.75 ? mix(dirty, tan, rnd(th, 12u))
             : pc < 0.92 ? mix(grey, tan, rnd(th, 12u) * 0.5)
             : mix(grey, vec3(0.30, 0.29, 0.29), rnd(th, 12u));
    vAlbedo = alb * mix(0.85, 1.08, rnd(th, 13u));
    vIce = pc < 0.45 ? 1.0 : (pc < 0.75 ? 0.45 : 0.1);

    gl_Position = uProj * uViewRot * vec4(rel, 1.0);
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
}

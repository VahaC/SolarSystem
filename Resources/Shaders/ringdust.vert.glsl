#version 330 core
// PROTOTYPE (2026-11-17): arcade flight mode — ice dust of the ring (RingDust.cs). No
// vertex attributes: everything is generated from gl_VertexID / gl_InstanceID.
//  uMode 0: sparkle grains. Each instance is one grain with a fixed offset inside a square
//           tile (side uPeriod, ring-plane coordinates relative to the anchor), wrapped to
//           the copy nearest the camera (like rock.vert.glsl). Height, size, facet and
//           existence are hashed from (instance, tile), so the wrap never repeats.
//  uMode 1: haze layers. Instance i is a big square in the ring plane at height
//           uLayerH[i]; the fragment shader integrates the slab slice it stands for.
//  uMode 2: crossing veil. One camera-facing quad uVeilDist in front of the eye; the
//           fragment shader marches the slab from the eye up to that distance.
// Positions are camera-relative world vectors (uViewRot has no translation).

uniform mat4 uProj;
uniform mat4 uViewRot;
uniform float uFcoef;
uniform int uMode;

uniform vec3 uCamRing;              // eye in ring coordinates (u, h, w) relative to the anchor
uniform vec3 uBasisU, uBasisN, uBasisW;
uniform float uRingInner, uRingOuter;

// Grains
uniform float uPeriod;
uniform uint uSeed;
uniform float uGrainSize;           // mean world radius
uniform float uPxPerRad;
uniform vec3 uCamRight, uCamUp;
uniform float uGrainGain;

// Haze
uniform float uLayerH[8];
uniform float uLayerExtent;

// Veil
uniform vec3 uVeilCenter, uVeilRight, uVeilUp;

out vec3 vRel;
out vec2 vUv;
flat out vec3 vFacet;
flat out float vGain;
flat out float vLayer;

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
    d *= mix(0.35, 1.0, smoothstep(0.22, 0.28, t));
    d *= mix(1.0, 0.04, smoothstep(0.62, 0.64, t) * (1.0 - smoothstep(0.69, 0.71, t)));
    d *= mix(1.0, 0.75, smoothstep(0.69, 0.71, t));
    d *= 1.0 - (smoothstep(0.885, 0.893, t) * (1.0 - smoothstep(0.903, 0.911, t)));
    d *= 0.68 + 0.32 * sin(t * 157.0 + sin(t * 41.0) * 2.0);
    return d;
}

const vec2 kCorner[6] = vec2[6](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

void emit(vec3 rel) {
    gl_Position = uProj * uViewRot * vec4(rel, 1.0);
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
}

void cull() {
    gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
    vRel = vec3(0); vUv = vec2(0); vFacet = vec3(0, 1, 0); vGain = 0.0; vLayer = 0.0;
}

void main() {
    vec2 corner = kCorner[gl_VertexID % 6];
    vUv = corner;
    vFacet = vec3(0, 1, 0);
    vGain = 1.0;
    vLayer = 0.0;

    if (uMode == 0) {
        uint idh = hash(uSeed * 0x9e3779b9u + uint(gl_InstanceID));
        vec2 o = vec2(rnd(idh, 0u), rnd(idh, 1u)) * uPeriod;
        vec2 c = uCamRing.xz;
        vec2 k = floor((c - o) / uPeriod + 0.5);
        vec2 q = o + k * uPeriod;
        ivec2 ki = ivec2(k);
        uint th = hash(idh ^ hash(uint(ki.x) * 73856093u ^ uint(ki.y) * 19349663u));

        float exist = step(rnd(th, 0u), ringDensity(length(q)) * 1.15);
        // Height: a near-Gaussian core (sigma ~0.018) plus a thin wide tail out to ~0.1.
        float g = (rnd(th, 1u) + rnd(th, 2u) + rnd(th, 3u) - 1.5) * 2.0;   // ~N(0, 1)
        float hgt = rnd(th, 4u) < 0.82 ? g * 0.018 : g * 0.05;
        vec3 rel = uBasisU * (q.x - c.x) + uBasisN * (hgt - uCamRing.y) + uBasisW * (q.y - c.y);
        float dist = length(rel);
        float R = 0.5 * uPeriod;
        float fade = 1.0 - smoothstep(0.55 * R, 0.95 * R, length(q - c));
        fade *= smoothstep(0.012, 0.045, dist);                  // gone right at the lens
        if (exist * fade <= 0.0) { cull(); return; }

        float size = uGrainSize * mix(0.5, 1.6, rnd(th, 5u) * rnd(th, 5u) + 0.2 * rnd(th, 6u));
        float px = size * uPxPerRad / max(dist, 1e-5);
        // Always a tiny soft disc: between ~0.9 and 2.4 px radius on screen. Sub-pixel
        // grains keep their light (dimmer), close grains never grow into snowflakes.
        float drawPx = clamp(px, 0.9, 2.4);
        float energy = min(1.0, (px * px) / (drawPx * drawPx));
        float w = drawPx * dist / uPxPerRad;
        rel += (uCamRight * corner.x + uCamUp * corner.y) * w;

        float z = rnd(th, 7u) * 2.0 - 1.0;
        float a = rnd(th, 8u) * 6.2831853;
        vFacet = vec3(sqrt(1.0 - z * z) * cos(a), z, sqrt(1.0 - z * z) * sin(a));
        vGain = uGrainGain * exist * fade * mix(0.35, 1.0, energy) * mix(0.5, 1.3, rnd(th, 9u));
        vLayer = rnd(th, 10u);                                    // tint variation
        vRel = rel;
        emit(rel);
        return;
    }

    if (uMode == 1) {
        float h = uLayerH[gl_InstanceID];
        vec2 xz = corner * uLayerExtent;
        vec3 rel = uBasisU * (xz.x - uCamRing.x) + uBasisN * (h - uCamRing.y) + uBasisW * (xz.y - uCamRing.z);
        vLayer = float(gl_InstanceID);
        vRel = rel;
        emit(rel);
        return;
    }

    // uMode == 2
    vec3 rel = uVeilCenter + uVeilRight * corner.x + uVeilUp * corner.y;
    vRel = rel;
    emit(rel);
}

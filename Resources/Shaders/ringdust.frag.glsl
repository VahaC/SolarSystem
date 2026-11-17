#version 330 core
// PROTOTYPE (2026-11-17): arcade flight mode — ice dust of the ring (RingDust.cs).
// Additive HDR output. Lighting: the Sun only, Henyey-Greenstein phase with a strong
// forward lobe (dust lights up when looking sunward) plus a weaker back lobe, the
// planet's shadow by a soft ray-vs-sphere test toward the Sun.
in vec3 vRel;
in vec2 vUv;
flat in vec3 vFacet;
flat in float vGain;
flat in float vLayer;

uniform int uMode;
uniform vec3 uCamRing;
uniform vec3 uBasisU, uBasisN, uBasisW;
uniform float uRingInner, uRingOuter;
uniform vec3 uSunRel;          // Sun minus eye
uniform vec3 uAnchorRel;       // anchor centre minus eye
uniform float uAnchorRadius;
uniform vec3 uIceColor, uTanColor;   // HDR tints (sun colour folded in)
uniform float uLitSide;        // 1 when the eye is on the sunlit face of the ring, else 0

// Haze
uniform float uLayerW[8];      // slab weight of each layer (profile * thickness)
uniform float uHazeGain;
uniform float uMuMin;
// Veil
uniform float uVeilDist;
uniform float uVeilGain;

out vec4 FragColor;

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

// Vertical density profile of the dust slab (1 at the plane).
float slab(float h) {
    return 0.82 * exp(-h * h / (2.0 * 0.018 * 0.018)) + 0.18 * exp(-h * h / (2.0 * 0.05 * 0.05));
}

// 1 = sunlit, 0 = inside the planet's shadow (soft edge).
float sunVisible(vec3 rel, vec3 L) {
    vec3 a = uAnchorRel - rel;
    float tca = dot(a, L);
    if (tca <= 0.0) return 1.0;
    float d = sqrt(max(dot(a, a) - tca * tca, 0.0));
    return smoothstep(uAnchorRadius * 0.985, uAnchorRadius * 1.03, d);
}

float hg(float c, float g) {
    float g2 = g * g;
    return (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5);
}

// Scattering toward the eye: c = cos(angle between sun->point light and point->eye).
float phase(float c) {
    float back = mix(0.12, 0.35, uLitSide) * hg(c, -0.3);
    return min(0.28 + 0.22 * hg(c, 0.72) + back, 6.0);
}

void main() {
    if (uMode == 0) {
        float r2 = dot(vUv, vUv);
        if (r2 > 1.0) discard;
        float disc = exp(-r2 * 3.2);
        vec3 L = normalize(uSunRel - vRel);
        vec3 V = normalize(-vRel);
        float vis = sunVisible(vRel, L);
        if (vis <= 0.0) discard;
        float c = dot(V, -L);
        // Glint: the grain's facet mirrors the Sun into the eye (two-sided facet).
        vec3 H = normalize(L + V);
        float nh = abs(dot(vFacet, H));
        float glint = pow(nh, 400.0) * 24.0 + pow(nh, 30.0) * 5.0 + pow(nh, 6.0) * 0.4;
        // Forward-scattered glitter: a second, wider facet response toward the Sun.
        float fwd = pow(max(c, 0.0), 6.0);
        float b = phase(c) * 0.22 + glint * (0.7 + 1.5 * fwd) + fwd * 0.8;
        vec3 col = mix(uIceColor, uTanColor, vLayer * 0.45);
        FragColor = vec4(col * b * vGain * vis * disc, 0.0);
        return;
    }

    if (uMode == 1) {
        int li = int(vLayer + 0.5);
        vec3 p = uCamRing + vec3(dot(vRel, uBasisU), dot(vRel, uBasisN), dot(vRel, uBasisW));
        float dens = ringDensity(length(p.xz));
        if (dens <= 0.0) discard;
        float dist = length(vRel);
        vec3 V = vRel / max(dist, 1e-6);
        float mu = abs(dot(V, uBasisN));
        // A layer the eye sits (almost) inside would flip between half-screens as the
        // eye crosses it; it fades out and the veil takes over close to the eye.
        float dh = abs(p.y - uCamRing.y);
        float k = smoothstep(0.012, 0.03, dh) * smoothstep(0.35, 0.7, dist);
        if (k <= 0.0) discard;
        // Rich up close, subtle far away.
        k *= mix(1.0, 0.3, smoothstep(0.6, 4.0, dist));
        vec3 L = normalize(uSunRel - vRel);
        float vis = sunVisible(vRel, L);
        if (vis <= 0.0) discard;
        float path = uLayerW[li] / max(mu, uMuMin);
        float b = uHazeGain * dens * path * phase(dot(-V, -L)) * vis * k;
        vec3 col = mix(uIceColor, uTanColor, 0.35);
        FragColor = vec4(col * b, 0.0);
        return;
    }

    // uMode == 2: march the slab from the eye toward this pixel, up to the veil quad.
    vec3 dir = normalize(vRel);
    float total = 0.0;
    const int N = 12;
    float dt = uVeilDist * 1.7 / float(N);
    vec3 dirRing = vec3(dot(dir, uBasisU), dot(dir, uBasisN), dot(dir, uBasisW));
    for (int i = 0; i < N; i++) {
        float t = (float(i) + 0.5) * dt;
        vec3 p = uCamRing + dirRing * t;
        total += slab(p.y) * ringDensity(length(p.xz)) * dt;
    }
    if (total <= 1e-5) discard;
    vec3 L = normalize(uSunRel);
    float vis = sunVisible(dir * uVeilDist, L);
    // Emphasise the long in-plane paths over the short ones across the slab, so the veil
    // reads as a glowing sheet around the horizon rather than a uniform fog.
    total *= total / (total + 0.1);
    float b = uVeilGain * total * phase(dot(-dir, -L)) * vis;
    vec3 col = mix(uIceColor, uTanColor, 0.3);
    FragColor = vec4(col * b, 0.0);
}

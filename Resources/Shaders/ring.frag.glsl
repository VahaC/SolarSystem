#version 330 core
in vec2 vUv; in vec3 vWorldPos; out vec4 fragColor;
uniform sampler2D uTex;
uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform vec3 uLightPos;
uniform int uHasShadow;
// Flight-mode close-up (uFlight >= 0.5). With uFlight = 0 the original path runs unchanged.
uniform float uFlight;
uniform float uTime;
uniform vec3 uCamPos;
uniform float uInnerRadius;
uniform float uOuterRadius;
uniform vec3 uRingNormal;

// ---------------------------------------------------------------- flight-mode helpers
uint hashU(uint x) {
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}
float hash1(int i) { return float(hashU(uint(i) * 0x9E3779B9u + 0x632BE5ABu)) * (1.0 / 4294967295.0); }
float hash2(ivec2 c) { return float(hashU(uint(c.x) * 0x8DA6B343u ^ hashU(uint(c.y) * 0xD8163841u))) * (1.0 / 4294967295.0); }

// 1D value noise in [-1, 1] with a smooth (quintic) fade.
float vnoise(float x) {
    float i = floor(x), f = x - i;
    float u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    return mix(hash1(int(i)), hash1(int(i) + 1), u) * 2.0 - 1.0;
}

// 2D value noise in [-1, 1], smooth (no visible cell blocks).
float vnoise2(vec2 p) {
    vec2 i = floor(p), f = p - i;
    vec2 u = f * f * (3.0 - 2.0 * f);
    ivec2 c = ivec2(i);
    float a = hash2(c), b = hash2(c + ivec2(1, 0));
    float d = hash2(c + ivec2(0, 1)), e = hash2(c + ivec2(1, 1));
    return mix(mix(a, b, u.x), mix(d, e, u.x), u.y) * 2.0 - 1.0;
}

// Henyey-Greenstein phase, normalised so g = 0 gives 1.
float hg(float cosT, float g) {
    float g2 = g * g;
    return (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * cosT, 1e-4), 1.5);
}

vec4 flightRing() {
    vec3 n = normalize(uRingNormal);
    vec3 rel = vWorldPos - uPlanetCenter;
    vec3 inPlane = rel - n * dot(rel, n);
    // True radius per fragment: perfect circles, no chord kinks from the 64-gon mesh.
    float r = length(inPlane);
    float span = uOuterRadius - uInnerRadius;
    float u = (r - uInnerRadius) / span;
    float inside = step(0.0, u) * step(u, 1.0);

    // Analytic derivatives of the radial coordinate -> correct mip selection.
    float dux = dFdx(u), duy = dFdy(u);
    vec2 tsz = vec2(textureSize(uTex, 0));
    float uc = clamp(u, 0.5 / tsz.x, 1.0 - 0.5 / tsz.x); // no Repeat-wrap bleed at the edges
    vec4 fine = textureGrad(uTex, vec2(uc, 0.5), vec2(dux, 0.0), vec2(duy, 0.0));
    // The texture carries per-texel RGB jitter that turns into pink / green fringes when
    // magnified. Keep the fine luminance (the bands) but take the hue from a blurred lookup.
    vec4 soft = textureGrad(uTex, vec2(uc, 0.5), vec2(dux * 48.0 + 24.0 / tsz.x, 0.0), vec2(duy * 48.0, 0.0));
    float lumF = dot(fine.rgb, vec3(0.299, 0.587, 0.114));
    float lumS = dot(soft.rgb, vec3(0.299, 0.587, 0.114));
    vec3 hue = soft.rgb / max(lumS, 1e-3);
    hue = mix(vec3(1.0), hue, 0.7);                 // keep the tint gentle
    vec3 col = hue * lumF;
    float a = fine.a;

    // ---- procedural ringlets: fade in where the texture runs out of detail,
    // and each octave fades out before it gets smaller than a pixel (no aliasing).
    float fwR = max(abs(dFdx(r)) + abs(dFdy(r)), 1e-7);
    float texel = span / tsz.x;
    float detail = 1.0 - smoothstep(texel * 1.0, texel * 6.0, fwR);
    float ring = 0.0;
    float wsum = 0.0;
    float lam = 0.024, amp = 1.0;
    for (int i = 0; i < 5; i++) {
        float w = 1.0 - smoothstep(0.25, 0.6, fwR / lam);
        ring += amp * w * vnoise(r / lam + float(i) * 17.31);
        wsum += amp;
        lam *= 0.37; amp *= 0.78;
    }
    ring = ring / wsum * detail;
    float m = clamp(1.0 + 0.55 * ring, 0.6, 1.4);
    col *= m;
    a = clamp(a * mix(1.0, m, 0.5), 0.0, 1.0);

    // ---- in-plane grain + sparse ice glints (world-locked, so no shimmer while moving)
    vec3 e1 = normalize(abs(n.z) < 0.9 ? cross(n, vec3(0.0, 0.0, 1.0)) : cross(n, vec3(1.0, 0.0, 0.0)));
    vec3 e2 = cross(n, e1);
    vec2 q = vec2(dot(inPlane, e1), dot(inPlane, e2));
    vec2 dqx = dFdx(q), dqy = dFdy(q);
    float fwQ = max(length(dqx), length(dqy));

    const float CELL = 0.0022;
    vec2 qc = q / CELL;
    ivec2 cid = ivec2(floor(qc));
    float h = hash2(cid);
    float grainFade = 1.0 - smoothstep(0.15, 0.5, fwQ / CELL);
    float grainFade2 = 1.0 - smoothstep(0.15, 0.5, fwQ / (CELL * 3.0));
    float grain = vnoise2(qc) * 0.22 * grainFade + vnoise2(qc / 3.0 + 31.7) * 0.18 * grainFade2;
    col *= 1.0 + grain;
    a = clamp(a * (1.0 + grain * 0.8), 0.0, 1.0);

    // ---- lighting of a sheet of ice particles
    vec3 L = normalize(uLightPos - vWorldPos);
    vec3 V = normalize(uCamPos - vWorldPos);
    float mu0 = dot(L, n);
    float mu = dot(V, n);
    float sunEl = abs(mu0);
    bool litSide = (mu0 * mu) >= 0.0;
    float cosFwd = dot(-V, L);                 // 1 = looking toward the Sun through the ring
    float opt = a;                             // optical-depth proxy

    float lit;
    if (litSide) {
        // Reflected light: stronger with a higher Sun, gentle opposition surge when the
        // Sun is behind the camera.
        lit = mix(0.75, 1.15, smoothstep(0.0, 0.45, sunEl));
        lit *= 1.0 + 0.25 * pow(max(-cosFwd, 0.0), 8.0);
    } else {
        // Unlit face: diffuse light transmitted through the sheet. Thin parts (Cassini
        // division, C ring) glow, dense parts stay dark; strong forward-scattering lobe.
        float trans = 4.0 * opt * (1.0 - opt);
        float fwdLobe = hg(cosFwd, 0.55) * 0.18;
        lit = 0.38 + trans * (0.5 + fwdLobe) * smoothstep(0.0, 0.25, sunEl + 0.05);
        lit = mix(lit, lit * 1.2, 1.0 - opt);
    }
    // Faint forward-scattered halo on both sides when looking toward the Sun.
    lit += 0.35 * pow(max(cosFwd, 0.0), 12.0) * (1.0 - opt * 0.5);

    // ---- soft-edged planet shadow
    float shade = 1.0;
    if (uHasShadow != 0) {
        vec3 toC = uPlanetCenter - vWorldPos;
        float t = dot(toC, L);
        if (t > 0.0) {
            float dist = length(toC - L * t);
            shade = mix(0.22, 1.0, smoothstep(uPlanetRadius * 0.975, uPlanetRadius * 1.02, dist));
        }
    }
    lit *= shade;
    col *= lit;

    // Glints: a few cells hold a bright ice crystal; they twinkle very slowly.
    float gl = 0.0;
    if (h > 0.965) {
        vec2 ctr = vec2(hash1(int(h * 1e6)), hash1(int(h * 1e6) + 3)) * 0.6 + 0.2;
        float d = length(fract(qc) - ctr);
        float tw = 0.6 + 0.4 * sin(uTime * 0.7 + h * 6283.0);
        gl = smoothstep(0.28, 0.0, d) * tw * grainFade;
        float sunK = litSide ? (0.4 + sunEl) : (0.3 + 1.2 * pow(max(cosFwd, 0.0), 4.0));
        gl *= sunK * shade;
    }
    col += vec3(0.9, 0.95, 1.0) * gl * 1.6;
    a = max(a, gl * 0.8 * inside);

    // ---- thin out right around the camera so the rocks and dust carry the close-up
    float dc = length(vWorldPos - uCamPos);
    a *= mix(0.4, 1.0, smoothstep(0.03, 0.4, dc));

    a *= inside;
    return vec4(col, a);
}

void main(){
    if (uFlight >= 0.5) {
        fragColor = flightRing();
        return;
    }
    vec4 c = texture(uTex, vUv);
    // V5: Saturn-on-ring shadow. Ray from the ring fragment toward the Sun
    // intersected with Saturn's bounding sphere; if the segment hits the sphere
    // (t in (0, 1)) the fragment is occluded and we darken it to a soft shadow.
    if (uHasShadow != 0) {
        vec3 d = uLightPos - vWorldPos;
        vec3 oc = vWorldPos - uPlanetCenter;
        float a = dot(d, d);
        float b = dot(oc, d);
        float cc = dot(oc, oc) - uPlanetRadius * uPlanetRadius;
        float disc = b * b - a * cc;
        if (disc > 0.0) {
            float sq = sqrt(disc);
            float t0 = (-b - sq) / a;
            float t1 = (-b + sq) / a;
            if ((t0 > 0.0 && t0 < 1.0) || (t1 > 0.0 && t1 < 1.0))
                c.rgb *= 0.25;
        }
    }
    fragColor = c;
}

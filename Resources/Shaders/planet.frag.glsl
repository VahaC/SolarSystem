#version 330 core
in vec3 vWorldPos; in vec3 vNormal; in vec2 vUv;
out vec4 fragColor;
uniform sampler2D uTex;
uniform sampler2D uNightTex;
uniform sampler2D uRingTex;
uniform sampler2D uOceanMask;
uniform int uHasNight;
uniform int uHasRing;
uniform int uHasOceanMask; // V15
uniform float uRingInner;
uniform float uRingOuter;
uniform vec3 uRingNormal;
uniform vec3 uLightPos; uniform vec3 uViewPos; uniform vec3 uLightColor; uniform vec3 uTint;
uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;

// V14: per-body PBR (Cook-Torrance / GGX). uPbrEnabled = 0 falls back to the
// legacy Phong specular term.
uniform int uPbrEnabled;
uniform float uRoughness;
uniform float uMetallic;

// V8: eclipse / body shadows. Each caster is (xyz=center, w=radius).
#define MAX_SHADOW_CASTERS 16
uniform int uShadowCount;
uniform vec4 uShadowSpheres[MAX_SHADOW_CASTERS];

// V9: per-body atmospheric scattering coefficients. uHasAtmosphere=0 disables.
uniform int uHasAtmosphere;
uniform vec3 uAtmosphereColor;
uniform float uAtmosphereStrength;

// Returns the maximum occlusion fraction (0=lit, 1=fully shadowed) caused by any
// other body intersecting the segment from `origin` to `lightPos`. Soft penumbra
// from a smoothstep around the silhouette; the shaded body itself is skipped.
// `radiusScale` inflates the caster radius to compensate for the vertex-stage
// silhouette expansion used in real-scale mode (planet.vert.glsl) — without it
// the umbra would be sub-pixel on the artificially enlarged receiver disc, so
// solar/lunar eclipses and Galilean transits would never become visible.
float bodyShadow(vec3 origin, vec3 lightPos, float radiusScale) {
    vec3 toLight = lightPos - origin;
    float dToLight = length(toLight);
    if (dToLight < 1e-4) return 0.0;
    vec3 ldir = toLight / dToLight;
    float occlusion = 0.0;
    for (int i = 0; i < uShadowCount; i++) {
        vec4 sph = uShadowSpheres[i];
        // Skip the body being shaded (matches via planet center).
        if (length(sph.xyz - uPlanetCenter) < max(uPlanetRadius, sph.w) * 0.5) continue;
        vec3 oc = sph.xyz - origin;
        float tca = dot(oc, ldir);
        if (tca <= 0.0 || tca >= dToLight) continue;
        float d2 = max(dot(oc, oc) - tca * tca, 0.0);
        float r = sph.w * radiusScale;
        if (d2 >= r * r) continue;
        float d = sqrt(d2);
        float soft = 1.0 - smoothstep(r * 0.85, r, d);
        occlusion = max(occlusion, soft);
    }
    return occlusion;
}

void main(){
    // Sphere normal from world geometry (independent of model-matrix conventions).
    vec3 N = normalize(vWorldPos - uPlanetCenter);
    vec3 L = normalize(uLightPos - vWorldPos);
    vec3 V = normalize(uViewPos - vWorldPos);
    vec3 R = reflect(-L, N);
    float NdotL = dot(N, L);
    float diff = max(NdotL, 0.0);
    vec3 base = texture(uTex, vUv).rgb * uTint;
    vec3 ambient = base * 0.18;
    // V5: ring-shadow attenuation on the lit hemisphere. Cast a ray from the
    // current fragment toward the Sun and intersect it with the ring plane; if
    // the hit point falls between the inner and outer ring radii, sample the
    // ring texture's alpha and use it as the occlusion fraction.
    float ringShadow = 1.0;
    if (uHasRing != 0 && NdotL > 0.0) {
        vec3 toSun = uLightPos - vWorldPos;
        float denom = dot(toSun, uRingNormal);
        if (abs(denom) > 1e-6) {
            float t = -dot(vWorldPos - uPlanetCenter, uRingNormal) / denom;
            if (t > 0.0 && t < 1.0) {
                vec3 hit = vWorldPos + t * toSun;
                float r = length(hit - uPlanetCenter);
                if (r > uRingInner && r < uRingOuter) {
                    float u = (r - uRingInner) / max(uRingOuter - uRingInner, 1e-4);
                    float a = texture(uRingTex, vec2(u, 0.5)).a;
                    ringShadow = clamp(1.0 - a, 0.0, 1.0);
                }
            }
        }
    }

    // V15: gate specular by an ocean mask (Earth-only by default). 1 = full
    // glint (water), 0 = matte (land); when the mask isn't bound the factor is
    // 1 so every fragment can still glint, preserving the legacy look.
    float oceanFactor = 1.0;
    if (uHasOceanMask != 0) oceanFactor = texture(uOceanMask, vUv).r;

    vec3 lit;
    vec3 specTerm;
    if (uPbrEnabled != 0) {
        // V14: Cook-Torrance with GGX D, Schlick-GGX G and Schlick F.
        vec3 H = normalize(L + V);
        float NdotV = max(dot(N, V), 1e-4);
        float NdotH = max(dot(N, H), 0.0);
        float VdotH = max(dot(V, H), 0.0);
        float r = clamp(uRoughness, 0.045, 1.0);
        float a = r * r;
        float a2 = a * a;
        float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
        float D = a2 / (3.14159265 * d * d);
        float k = (r + 1.0); k = k * k * 0.125;
        float Gv = NdotV / (NdotV * (1.0 - k) + k);
        float Gl = diff / max(diff * (1.0 - k) + k, 1e-4);
        float G = Gv * Gl;
        vec3 F0 = mix(vec3(0.04), base, uMetallic);
        vec3 F = F0 + (vec3(1.0) - F0) * pow(1.0 - VdotH, 5.0);
        vec3 specPbr = (D * G * F) / max(4.0 * NdotV * diff, 1e-4);
        vec3 kd = (vec3(1.0) - F) * (1.0 - uMetallic);
        lit = kd * base * uLightColor * diff * ringShadow;
        specTerm = specPbr * uLightColor * diff * ringShadow * oceanFactor;
    } else {
        float spec = pow(max(dot(V, R), 0.0), 24.0) * 0.15 * oceanFactor;
        lit = base * uLightColor * diff * ringShadow;
        specTerm = uLightColor * spec * diff * ringShadow;
    }

    // V8: attenuate direct sunlight by any other body intersecting the segment
    // toward the Sun (lunar/solar eclipses, Galilean transit shadows, etc.).
    // In real-scale mode the vertex stage inflates the receiver's silhouette
    // outward from uPlanetCenter by k = |vWorldPos - center| / uPlanetRadius
    // (it equals 1 in compressed mode). Reconstruct the true surface point as
    // the shadow ray origin and scale caster radii by the same factor so the
    // umbra stays visually proportional to the rendered (enlarged) disc.
    float inflation = length(vWorldPos - uPlanetCenter) / max(uPlanetRadius, 1e-8);
    vec3 shadowOrigin = uPlanetCenter + N * uPlanetRadius;
    float eclipse = 1.0 - bodyShadow(shadowOrigin, uLightPos, inflation);
    vec3 color = ambient + (lit + specTerm) * eclipse;
    // V4: night-side emissive map (city lights). Only contributes on the dark
    // side of the terminator and fades smoothly across it.
    if (uHasNight != 0) {
        vec3 night = texture(uNightTex, vUv).rgb;
        float nightMix = 1.0 - smoothstep(-0.05, 0.2, NdotL);
        color += night * nightMix;
    }
    // V9: cheap single-scatter atmospheric scattering. Rayleigh + Mie phase
    // functions modulate a Fresnel rim glow on the lit hemisphere; per-body
    // colour and strength let the same shader serve Earth, Mars, Venus,
    // Titan and Neptune.
    if (uHasAtmosphere != 0) {
        float NdotV = max(dot(N, V), 0.0);
        float rim = pow(1.0 - NdotV, 3.0);
        float mu = dot(L, V);
        float rayleigh = 0.75 * (1.0 + mu * mu);
        float g = 0.76;
        float g2 = g * g;
        float mie = (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * mu, 1e-4), 1.5);
        // Soft terminator wrap so the rim doesn't snap off at exactly NdotL=0.
        float sunFacing = clamp(NdotL + 0.25, 0.0, 1.0);
        vec3 scatter = uAtmosphereColor * rayleigh * rim * sunFacing * uAtmosphereStrength;
        scatter += vec3(1.0, 0.95, 0.85) * mie * 0.004 * sunFacing * rim * uAtmosphereStrength;
        color += scatter * eclipse;
    }
    fragColor = vec4(color, 1.0);
}

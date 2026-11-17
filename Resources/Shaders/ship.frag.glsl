#version 330 core
// PROTOTYPE (2026-11-17): arcade flight mode — the player's ship (fragment stage).
// Cook-Torrance GGX lit by the Sun (point light), anchor-planet shadow and bounce light,
// a cheap planet reflection for the metals, procedural panel lines and HDR emissives.
in vec3 vWorldPos;
in vec3 vNormal;
in vec3 vLocalPos;
in vec3 vLocalNormal;
flat in int vMat;
out vec4 fragColor;

uniform vec3 uSunPos;
uniform vec3 uViewPos;
uniform vec3 uAnchorPos;
uniform float uAnchorRadius;
uniform vec3 uAnchorColor;
uniform float uTime;
uniform float uThrottle;
uniform float uBoost;
uniform float uSunIntensity;

// Material ids (must match ShipRenderer.Mat).
const int M_FUSELAGE = 0;
const int M_WING = 1;
const int M_CANOPY = 3;
const int M_ENGINE = 4;
const int M_BARE = 5;
const int M_NAV_L = 6;
const int M_NAV_R = 7;
const int M_CORE = 9;
const int M_NACELLE = 10;
const int M_FIN = 11;
const int M_REAR = 12;

const float PI = 3.14159265;
const vec3 SUN_COLOR = vec3(1.0, 0.96, 0.88);
const vec3 ACCENT = vec3(0.95, 0.36, 0.05);      // orange accent paint
const vec3 GLOW = vec3(0.25, 0.85, 1.0);         // cyan light strips

float hash13(vec3 p) {
    p = fract(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}

// 1 on a line at every multiple of `cell` along `coord`, anti-aliased with fwidth.
float gridLine(float coord, float cell, float width) {
    float d = abs(fract(coord / cell + 0.5) - 0.5) * cell;
    float w = fwidth(coord) * 1.2;
    return 1.0 - smoothstep(width, width + w, d);
}

// 1 inside [a, b] along `coord`, anti-aliased.
float band(float coord, float a, float b) {
    float w = fwidth(coord) * 1.0 + 1e-5;
    return smoothstep(a - w, a, coord) * (1.0 - smoothstep(b, b + w, coord));
}

// Panel seams: a line from axis k only shows where the surface crosses planes of
// constant k at a steep angle (otherwise it would smear into a wide stain).
float panelLines(vec3 p, vec3 n, vec3 cell, float width) {
    vec3 an = abs(n);
    float lx = gridLine(abs(p.x) + 0.02, cell.x, width) * (1.0 - smoothstep(0.55, 0.85, an.x));
    float ly = gridLine(p.y + 0.5, cell.y, width) * (1.0 - smoothstep(0.55, 0.85, an.y));
    float lz = gridLine(p.z + 0.5, cell.z, width) * (1.0 - smoothstep(0.55, 0.85, an.z));
    return max(lx, max(ly, lz));
}

float anchorShadow(vec3 p) {
    vec3 toSun = uSunPos - p;
    float dSun = length(toSun);
    vec3 l = toSun / max(dSun, 1e-6);
    vec3 oc = uAnchorPos - p;
    float tca = dot(oc, l);
    if (tca <= 0.0 || tca >= dSun || uAnchorRadius <= 0.0) return 1.0;
    float d = sqrt(max(dot(oc, oc) - tca * tca, 0.0));
    float r = uAnchorRadius;
    return smoothstep(r * 0.985, r * 1.025, d);
}

void main() {
    vec3 p = vLocalPos;
    vec3 nl = normalize(vLocalNormal);
    vec3 N = normalize(vNormal);
    vec3 V = normalize(uViewPos - vWorldPos);
    vec3 L = normalize(uSunPos - vWorldPos);

    // ---- material ----------------------------------------------------------------
    vec3 albedo = vec3(0.13, 0.14, 0.16);
    float metal = 0.6;
    float rough = 0.3;
    vec3 emit = vec3(0.0);
    bool painted = true;              // gets panel lines + variation
    vec3 cell = vec3(0.105, 0.065, 0.082);

    float level = mix(1.6, 6.0, clamp(uThrottle, 0.0, 1.0)) + 5.0 * clamp(uBoost, 0.0, 1.0);
    float flicker = 0.93 + 0.07 * sin(uTime * 57.0 + p.x * 30.0) * sin(uTime * 23.0);

    if (vMat == M_FUSELAGE) {
        // Thin cyan light strip along both flanks, forward of the wing root.
        float side = smoothstep(0.6, 0.8, abs(nl.x));
        float strip = band(p.y, -0.0045, 0.0045) * side * band(p.z, -0.34, 0.06);
        emit += GLOW * 3.2 * strip;
        // Accent chevron on the nose top.
        float chev = band(p.z + abs(p.x) * 1.2, -0.37, -0.345) * step(0.0, nl.y);
        albedo = mix(albedo, ACCENT, chev);
        metal = mix(metal, 0.2, chev);
    } else if (vMat == M_WING) {
        albedo = vec3(0.42, 0.44, 0.47);
        metal = 0.35; rough = 0.38;
        float stripe = band(abs(p.x), 0.305, 0.335);
        albedo = mix(albedo, ACCENT, stripe);
        cell = vec3(0.13, 0.5, 0.12);
    } else if (vMat == M_NACELLE) {
        albedo = vec3(0.17, 0.175, 0.19);
        metal = 0.65; rough = 0.28;
        float ring = band(p.z, -0.045, -0.02);
        albedo = mix(albedo, ACCENT, ring);
        metal = mix(metal, 0.2, ring);
        float lightBand = band(p.z, 0.352, 0.364);
        emit += GLOW * (1.5 + 2.5 * uThrottle + 3.0 * uBoost) * lightBand * flicker;
        cell = vec3(0.4, 0.4, 0.075);
    } else if (vMat == M_FIN) {
        albedo = vec3(0.42, 0.44, 0.47);
        metal = 0.35; rough = 0.38;
        float tip = smoothstep(0.205, 0.207, p.y);
        albedo = mix(albedo, ACCENT, tip);
        cell = vec3(0.4, 0.075, 0.11);
    } else if (vMat == M_REAR) {
        albedo = vec3(0.05, 0.05, 0.055);
        metal = 0.9; rough = 0.45;
        float slit = band(p.y, 0.0, 0.008) * band(abs(p.x), 0.0, 0.045);
        emit += GLOW * 4.0 * slit * flicker;
        painted = false;
    } else if (vMat == M_BARE) {
        // Bare titanium: nozzle lips and intakes, heat-tinted toward the exhaust.
        albedo = vec3(0.32, 0.31, 0.30);
        metal = 1.0; rough = 0.24;
        float heat = smoothstep(0.38, 0.47, p.z);
        albedo = mix(albedo, vec3(0.22, 0.2, 0.32), heat * 0.7);
        emit += vec3(1.0, 0.42, 0.12) * 0.35 * heat * uThrottle;
        if (p.z < 0.0) albedo *= 0.25;        // intakes are dark inside
        painted = false;
    } else if (vMat == M_CANOPY) {
        albedo = vec3(0.006, 0.01, 0.016);
        metal = 0.0; rough = 0.05;
        painted = false;
        // Canopy frame: centre ridge and two cross bars.
        float frame = max(band(abs(p.x), 0.0, 0.0035),
                      max(band(p.z, -0.145, -0.135), band(p.z, -0.035, -0.025)));
        if (frame > 0.0) {
            albedo = mix(albedo, vec3(0.06, 0.065, 0.075), frame);
            metal = mix(metal, 0.8, frame);
            rough = mix(rough, 0.35, frame);
        }
        // Instrument glow from inside, strongest low in the canopy.
        float low = 1.0 - smoothstep(0.06, 0.11, p.y);
        emit += (vec3(0.1, 0.45, 0.8) * 0.35 + vec3(1.0, 0.55, 0.15) * 0.18 * low) * (1.0 - frame);
    } else if (vMat == M_ENGINE) {
        float depth = clamp((0.47 - p.z) / 0.10, 0.0, 1.0);
        vec3 hot = mix(vec3(0.15, 0.7, 1.0), vec3(1.0, 0.96, 0.9), pow(depth, 1.3));
        emit = hot * level * mix(0.3, 1.0, depth * depth) * flicker;
        albedo = vec3(0.02); metal = 1.0; rough = 0.5;
        painted = false;
    } else if (vMat == M_CORE) {
        emit = vec3(1.0, 0.97, 0.92) * level * 1.5 * flicker;
        albedo = vec3(0.0);
        painted = false;
    } else if (vMat == M_NAV_L || vMat == M_NAV_R) {
        float phase = fract(uTime * 0.8 + (vMat == M_NAV_R ? 0.5 : 0.0));
        float on = phase < 0.12 ? 1.0 : 0.06;
        vec3 c = vMat == M_NAV_L ? vec3(1.0, 0.08, 0.04) : vec3(0.08, 1.0, 0.2);
        emit = c * 9.0 * on;
        albedo = c * 0.2; metal = 0.0; rough = 0.2;
        painted = false;
    }

    float seam = 0.0;
    if (painted) {
        // Per-panel tone and roughness variation, then crisp seams.
        vec3 pid = floor(vec3(abs(p.x) + 0.02, p.y + 0.5, p.z + 0.5) / cell);
        float h = hash13(pid * 17.0 + float(vMat) * 3.1);
        albedo *= 0.88 + 0.24 * h;
        rough = clamp(rough + (h - 0.5) * 0.12, 0.08, 1.0);
        seam = panelLines(p, nl, cell, 0.0016);
        albedo *= 1.0 - 0.6 * seam;
        rough = mix(rough, 0.8, seam);
    }

    // ---- lighting ----------------------------------------------------------------
    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 1e-4);
    vec3 H = normalize(L + V);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);
    float r = clamp(rough, 0.045, 1.0);
    float a = r * r;
    float a2 = a * a;
    float dd = NdotH * NdotH * (a2 - 1.0) + 1.0;
    float D = a2 / (PI * dd * dd);
    float k = (r + 1.0); k = k * k * 0.125;
    float G = (NdotV / (NdotV * (1.0 - k) + k)) * (NdotL / max(NdotL * (1.0 - k) + k, 1e-4));
    vec3 F0 = mix(vec3(0.04), albedo, metal);
    vec3 F = F0 + (vec3(1.0) - F0) * pow(1.0 - VdotH, 5.0);
    vec3 spec = (D * G * F) / max(4.0 * NdotV * NdotL, 1e-4);
    vec3 kd = (vec3(1.0) - F) * (1.0 - metal);
    vec3 sun = SUN_COLOR * uSunIntensity;
    float shadow = anchorShadow(vWorldPos);
    vec3 direct = (kd * albedo + spec) * sun * NdotL * shadow;

    // Anchor planet: bounce light and its reflection in the metal.
    vec3 toA = uAnchorPos - vWorldPos;
    float dA = max(length(toA), 1e-4);
    vec3 aDir = toA / dA;
    vec3 sunFromA = normalize(uSunPos - uAnchorPos);
    float planetLit = clamp(0.5 + 0.5 * dot(sunFromA, -aDir), 0.0, 1.0);   // lit fraction we see
    float sinAng = clamp(uAnchorRadius / dA, 0.0, 1.0);
    float solid = sinAng * sinAng;
    vec3 planetRad = uAnchorColor * planetLit * uSunIntensity * 0.55;
    float facing = max(dot(N, aDir), 0.0);
    vec3 bounce = albedo * (1.0 - metal * 0.6) * planetRad * facing * solid * 1.6;

    vec3 R = reflect(-V, N);
    float cosAng = sqrt(max(1.0 - sinAng * sinAng, 0.0));
    float inPlanet = smoothstep(cosAng - 0.02 * (1.0 + r * 4.0), cosAng + 0.01, dot(R, aDir));
    vec3 Fv = F0 + (vec3(1.0) - F0) * pow(1.0 - NdotV, 5.0);
    vec3 space = mix(vec3(0.01, 0.012, 0.02), vec3(0.05, 0.06, 0.09), R.y * 0.5 + 0.5);
    vec3 env = (planetRad * inPlanet + space) * Fv * (1.0 - r * 0.7);

    vec3 ambient = albedo * vec3(0.012, 0.014, 0.02);

    // Faint cool rim so the dark hull still reads against space on the night side.
    // Under boost it brightens so the hull stays a readable silhouette against the plumes.
    vec3 rimFill = vec3(0.03, 0.045, 0.08) * (1.0 + 3.0 * uBoost) * pow(1.0 - NdotV, 3.0);

    vec3 color = direct + bounce + env + ambient + rimFill;

    if (vMat == M_CANOPY) {
        // Fresnel rim on the glass picks up a cool sky tint plus the sun when lit.
        float rim = pow(1.0 - NdotV, 4.0);
        color += vec3(0.25, 0.55, 1.0) * rim * (0.25 + 0.75 * shadow * NdotL + 0.3 * planetLit * solid * 4.0);
    }

    color += emit;
    fragColor = vec4(color, 1.0);
}

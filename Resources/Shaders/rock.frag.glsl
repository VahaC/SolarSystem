#version 330 core
// PROTOTYPE (2026-11-17): arcade flight mode — ring rock shading. Sun = only light
// (diffuse + tight ice glint), low ambient, bounce light from the anchor planet and the
// planet's soft shadow. All positions are camera-relative.
in vec3 vRel;
in vec3 vNormal;
in vec3 vObj;
flat in vec3 vAlbedo;
flat in float vIce;
flat in float vSize;
out vec4 fragColor;

uniform vec3 uSunRel;        // Sun - eye
uniform vec3 uAnchorRel;     // anchor centre - eye
uniform float uAnchorRadius;
uniform vec3 uAnchorColor;
uniform vec3 uSunColor;

float h3(vec3 p) { return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453); }
float vnoise(vec3 p) {
    vec3 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(h3(i), h3(i + vec3(1,0,0)), f.x), mix(h3(i + vec3(0,1,0)), h3(i + vec3(1,1,0)), f.x), f.y),
               mix(mix(h3(i + vec3(0,0,1)), h3(i + vec3(1,0,1)), f.x), mix(h3(i + vec3(0,1,1)), h3(i + vec3(1,1,1)), f.x), f.y), f.z);
}

// Craters: one random bowl (with a raised rim) per cell of a 3D lattice, about half the
// cells empty. Only the 8 cells nearest p are tested. Returns height (rock-radius units)
// in .x and "crater floor" amount in .y.
vec2 craters(vec3 p) {
    vec3 b = floor(p - 0.5);
    vec2 r = vec2(0.0);
    for (int i = 0; i < 8; i++) {
        vec3 cell = b + vec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        if (h3(cell + 3.3) < 0.45) continue;
        vec3 c = cell + vec3(h3(cell), h3(cell + 11.3), h3(cell + 27.1));
        float rad = 0.22 + 0.28 * h3(cell + 5.7);
        float d = length(p - c) / rad;
        if (d > 1.6) continue;
        float bowl = d < 1.0 ? (d * d - 1.0) : 0.0;
        float rim = exp(-(d - 1.0) * (d - 1.0) * 14.0);
        r.x += (bowl * 0.55 + rim * 0.18) * rad;
        r.y = max(r.y, 1.0 - smoothstep(0.6, 1.0, d));
    }
    return r;
}

void main() {
    vec3 N = normalize(vNormal);
    vec3 V = normalize(-vRel);
    vec3 toSun = uSunRel - vRel;
    vec3 L = normalize(toSun);

    // Surface noise: blotchy dirt + fine grain.
    float n1 = vnoise(vObj * 1.7);
    float n2 = vnoise(vObj * 7.0);
    vec3 alb = vAlbedo * (0.72 + 0.38 * n1) * (0.9 + 0.2 * n2);
    alb = mix(alb, alb * vec3(0.82, 0.74, 0.64), smoothstep(0.55, 0.8, n1) * (1.0 - vIce * 0.6));

    // Big rocks only (vSize is per instance, so the branch is coherent): craters, cracks
    // and fine grain as a height field, turned into a bumped normal with screen-space
    // derivatives (surface-gradient bump, no tangents needed), plus dust tones and
    // cavity darkening. Octaves fade out when they get smaller than a pixel.
    float detail = smoothstep(0.018, 0.05, vSize);
    if (detail > 0.0) {
        float fw = length(fwidth(vObj)) + 1e-5;             // vObj units per pixel
        vec2 cr = craters(vObj * 0.6);
        float crack = 1.0 - smoothstep(0.0, 0.035, abs(vnoise(vObj * 0.9 + 4.1) - 0.5));
        crack *= smoothstep(0.35, 0.65, vnoise(vObj * 0.35 + 9.0));
        float g1 = vnoise(vObj * 4.0) - 0.5;
        float g2 = vnoise(vObj * 11.0) - 0.5;
        float w1 = 1.0 - smoothstep(0.25, 0.6, fw * 4.0);
        float w2 = 1.0 - smoothstep(0.25, 0.6, fw * 11.0);
        float wc = 1.0 - smoothstep(0.3, 0.8, fw * 0.6);
        // Height in rock-radius units (vObj is 3 units per radius).
        float hr = (cr.x * wc - crack * 0.012 * wc) * (1.0 / 1.667) + g1 * 0.018 * w1 + g2 * 0.006 * w2;
        float h = hr * vSize * detail;                       // world units
        vec3 dpdx = dFdx(vRel), dpdy = dFdy(vRel);
        vec3 r1 = cross(dpdy, N), r2 = cross(N, dpdx);
        float det = dot(dpdx, r1);
        if (abs(det) > 1e-14) {
            vec3 surfGrad = (dFdx(h) * r1 + dFdy(h) * r2) / det;
            N = normalize(N - surfGrad);
        }
        // Dust tone in crater floors and cracks, brighter fresh rims, cavity darkening.
        float dusty = clamp(cr.y * 0.8 * wc + crack * 0.7 * wc, 0.0, 1.0);
        vec3 dust = vAlbedo * vec3(0.62, 0.56, 0.50);
        alb = mix(alb, dust, dusty * detail * 0.65);
        float tone = vnoise(vObj * 0.5 + 2.0);
        alb *= mix(1.0, 0.78 + 0.34 * tone, detail);
        alb *= mix(1.0, 0.7 + 0.3 * smoothstep(-0.08, 0.02, hr), detail);
        alb *= mix(1.0, 0.9, detail * vIce);                 // keep big ice off pure white
    }

    // Planet shadow: ray from the fragment toward the Sun against the anchor sphere.
    vec3 oc = uAnchorRel - vRel;
    float dSun = length(toSun);
    float tca = dot(oc, L);
    float shadow = 1.0;
    if (tca > 0.0 && tca < dSun) {
        float d = sqrt(max(dot(oc, oc) - tca * tca, 0.0));
        shadow = smoothstep(uAnchorRadius * 0.96, uAnchorRadius * 1.03, d);
    }

    float NdotL = dot(N, L);
    float diff = max(NdotL, 0.0);
    // Soft wrap so the terminator on low-poly facets is less harsh.
    float wrap = max((NdotL + 0.25) / 1.25, 0.0) * 0.25 + diff * 0.75;

    // Tight specular glint (stronger on ice), Blinn-Phong normalised-ish.
    vec3 H = normalize(L + V);
    float NdotH = max(dot(N, H), 0.0);
    float shin = mix(40.0, 420.0, vIce);
    float spec = pow(NdotH, shin) * mix(0.05, 1.6, vIce) * (0.5 + n2) * diff;
    float fres = pow(1.0 - max(dot(N, V), 0.0), 5.0);
    spec += fres * 0.12 * vIce * diff;

    vec3 sun = uSunColor * shadow;
    vec3 col = alb * sun * wrap + sun * spec;

    // Bounce light from the planet's lit side.
    vec3 toPlanet = normalize(uAnchorRel - vRel);
    float planetLit = clamp(0.5 - 0.5 * dot(toPlanet, normalize(uSunRel - uAnchorRel)), 0.0, 1.0);
    float pDist = length(uAnchorRel - vRel) / uAnchorRadius;
    float bounce = planetLit * (0.5 + 0.5 * dot(N, toPlanet)) * 0.32 / (pDist * pDist);
    col += alb * uAnchorColor * bounce;

    // Ambient (starlight + ring-shine) and a cool forward-scatter rim for ice.
    col += alb * vec3(0.030, 0.032, 0.038);
    float back = pow(max(dot(-L, V), 0.0), 6.0) * fres;
    col += vec3(0.55, 0.62, 0.7) * back * 0.35 * vIce * shadow;

    // Depth cue: distant chunks lose a little contrast so the field reads in layers.
    float far = smoothstep(0.4, 3.0, length(vRel));
    col = mix(col, col * 0.6 + vec3(0.012, 0.013, 0.016), far * 0.6);

    fragColor = vec4(col, 1.0);
}

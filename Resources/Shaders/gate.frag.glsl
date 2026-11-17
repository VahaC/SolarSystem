#version 330 core
// PROTOTYPE (2026-11-17): arcade flight mode — gate race: gate visuals fragment shader.
// Output is premultiplied HDR colour for glBlendFunc(ONE, ONE); values above 1 bloom.
//
// Modes (vUv meaning in brackets):
//   0 rim      — billboarded tube along a circle (x = 0..1 around, y = across -1..1,
//                z = pattern 0 rim / 1 outer arcs / 2 finish checker, w = rotation in turns)
//   1 membrane — disc in the gate plane        (xy = -1..1, |xy| = 1 is the rim,
//                z = inward flow 0..1, w = finish spokes 0..1)
//   2 glow     — round soft sprite              (xy = -1..1)
//   3 star     — beacon star with spikes        (xy = -1..1)
//   4 shock    — expanding ring in a plane      (xy = -1..1, z = ring thickness)
//   5 chevron  — arrow pointing to +x           (xy = -1..1)
//   6 beam     — light pillar / spark streak    (x = bright end 0 .. tail 1, y = across -1..1)
in vec4 vUv;
in vec4 vCol;
flat in int vMode;

uniform float uTime;

out vec4 FragColor;

const float TAU = 6.2831853;

vec3 rim(vec4 uv){
    float y = uv.y;
    float core = exp(-y * y * 30.0);
    float hot  = exp(-y * y * 140.0);
    float glow = exp(-y * y * 3.5);
    float edge = 1.0 - smoothstep(0.75, 1.0, abs(y));
    float s = uv.x + uv.w;
    int pat = int(uv.z + 0.5);
    float a;
    vec3 tint = vCol.rgb;
    if (pat == 1) {
        // Three long arcs with fine tick marks, counter-rotating outside the rim.
        float f = fract(s * 3.0);
        float arc = smoothstep(0.0, 0.03, f) * (1.0 - smoothstep(0.42, 0.47, f));
        float ticks = 0.55 + 0.45 * smoothstep(0.3, 0.5, abs(fract(s * 72.0) - 0.5) * 2.0);
        a = (exp(-y * y * 45.0) * ticks + glow * 0.12) * arc;
        return tint * a * edge;
    }
    // Segmented energy band with two highlights sweeping around it.
    float f = fract(s * 20.0);
    float seg = smoothstep(0.0, 0.07, f) * (1.0 - smoothstep(0.80, 0.88, f));
    float sweep = pow(0.5 + 0.5 * cos(TAU * (uv.x * 2.0 - uTime * 0.45)), 10.0);
    a = core * (0.35 + 0.65 * seg) * (1.0 + 1.6 * sweep) + glow * 0.30;
    vec3 c = tint * a + vec3(1.0) * hot * (0.25 + 0.5 * sweep) * seg;
    if (pat == 2) {
        // Finish gate: a gold / white checker band.
        float chk = (fract(s * 32.0) < 0.5) == (y < 0.0) ? 1.0 : 0.0;
        c = tint * (core * mix(0.35, 1.25, chk) + glow * 0.35) + vec3(1.0, 0.95, 0.85) * core * chk * 0.7;
    }
    return c * edge;
}

vec3 membrane(vec4 uv){
    vec2 p = uv.xy;
    float r = length(p);
    if (r >= 1.0) return vec3(0.0);
    float ang = atan(p.y, p.x);
    float shimmer = 0.5 + 0.5 * sin(r * 26.0 - uTime * 2.3 + 2.0 * sin(ang * 3.0 + uTime * 0.8));
    float shimmer2 = 0.5 + 0.5 * sin(ang * 7.0 - uTime * 1.1 + r * 9.0);
    float a = 0.035 + 0.045 * shimmer * shimmer2 + 0.45 * pow(r, 10.0);
    // Rings contracting towards the centre: "fly in here".
    float rings = pow(0.5 + 0.5 * cos(TAU * (r * 2.2 + uTime * 1.3)), 12.0);
    a += uv.z * rings * 0.40 * smoothstep(0.08, 0.55, r);
    // Finish: turning spokes.
    float spokes = pow(abs(cos(ang * 6.0 + uTime * 0.6)), 40.0) * smoothstep(0.12, 0.45, r);
    a += uv.w * spokes * 0.35;
    a *= 1.0 - smoothstep(0.94, 1.0, r);
    return vCol.rgb * a;
}

void main(){
    vec4 uv = vUv;
    vec3 c;
    if (vMode == 0) {
        c = rim(uv);
    } else if (vMode == 1) {
        c = membrane(uv);
    } else if (vMode == 2) {
        float r2 = dot(uv.xy, uv.xy);
        float a = exp(-r2 * 9.0) + 0.25 * exp(-r2 * 2.5);
        a *= 1.0 - smoothstep(0.5, 1.0, r2);
        c = vCol.rgb * a + vec3(1.0) * exp(-r2 * 60.0) * 0.6 * length(vCol.rgb);
    } else if (vMode == 3) {
        vec2 p = uv.xy;
        float r2 = dot(p, p);
        float a = exp(-r2 * 40.0) * 2.0 + exp(-r2 * 6.0) * 0.35;
        float sx = exp(-abs(p.y) * 60.0) * pow(max(1.0 - abs(p.x), 0.0), 3.0);
        float sy = exp(-abs(p.x) * 60.0) * pow(max(1.0 - abs(p.y), 0.0), 3.0);
        a += (sx + sy) * 0.8;
        a *= 1.0 - smoothstep(0.85, 1.0, max(abs(p.x), abs(p.y)));
        c = vCol.rgb * a + vec3(1.0) * exp(-r2 * 200.0) * length(vCol.rgb) * 0.5;
    } else if (vMode == 4) {
        float r = length(uv.xy);
        float k1 = (r - 0.86) / max(uv.z, 0.005);
        float k2 = (r - 0.65) / 0.25;
        float a = exp(-k1 * k1) + 0.10 * exp(-k2 * k2);
        a *= 1.0 - smoothstep(0.95, 1.0, r);
        c = vCol.rgb * a;
    } else if (vMode == 5) {
        float d = uv.x - (0.40 - abs(uv.y) * 0.95);
        float a = exp(-d * d * 20.0) + 0.22 * exp(-d * d * 4.0);
        a *= 1.0 - smoothstep(0.65, 1.0, abs(uv.y));
        a *= 1.0 - smoothstep(0.80, 1.0, abs(uv.x));
        c = vCol.rgb * a;
    } else {
        float x = clamp(uv.x, 0.0, 1.0);
        float y = uv.y;
        float pulse = 0.75 + 0.25 * pow(0.5 + 0.5 * cos(TAU * (x * 3.0 - uTime * 0.9)), 4.0);
        float a = (exp(-y * y * 25.0) + 0.25 * exp(-y * y * 3.0)) * pow(1.0 - x, 1.6) * pulse;
        a *= 1.0 - smoothstep(0.8, 1.0, abs(y));
        c = vCol.rgb * a;
    }
    c *= vCol.a;
    FragColor = vec4(max(c, vec3(0.0)), 0.0);
}

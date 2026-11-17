#version 330 core
// PROTOTYPE (2026-11-17): arcade flight mode — ship effects fragment shader. Output is
// premultiplied HDR colour for glBlendFunc(ONE, ONE); values above 1 feed the bloom.
//
// Modes (vUv meaning in brackets):
//   0 ribbon  — light trail          (x = age 0..1, y = across -1..1)
//   1 plume   — engine exhaust core  (x = nozzle 0 .. tail 1, y = across -1..1)
//   2 glow    — round soft sprite    (x, y = -1..1)
//   3 ring    — boost shock ring     (x, y = -1..1 in the ring plane)
//   4 streak  — dust / ice speed line(x = head 0 .. tail 1, y = across -1..1)
//   5 flare   — anamorphic streak    (x, y = -1..1, quad is very wide)
in vec2 vUv;
in vec4 vCol;
flat in int vMode;

uniform float uTime;
uniform float uBoost;

out vec4 FragColor;

vec3 plume(vec2 uv){
    float x = clamp(uv.x, 0.0, 1.0);
    float y = uv.y;
    // Flame silhouette: leaves the nozzle a little narrower, swells, then tapers.
    float swell = mix(0.6, 1.0, smoothstep(0.0, 0.2, x));
    float r = swell * pow(max(1.0 - x, 0.0), 0.65) + 1e-3;
    float d = abs(y) / r;

    // Shock diamonds: the hot core pinches and flares periodically under boost.
    float ph = x * (4.0 + 2.5 * uBoost) * 6.2831853 - uTime * 0.6;
    float diamond = pow(0.5 + 0.5 * cos(ph), 5.0);
    float pinch = mix(1.0, 0.45 + 0.75 * diamond, uBoost);
    float core = exp(-d * d * 9.0 / (pinch * pinch)) * pow(1.0 - x, 1.4);
    float glow = exp(-d * d * 2.0) * pow(1.0 - x, 1.0);

    // Turbulent flicker travelling down the flame.
    float ripple = 0.86 + 0.14 * sin(x * 41.0 - uTime * 73.0) * sin(x * 17.0 + uTime * 37.0);
    core *= ripple * (1.0 + uBoost * 1.4 * diamond * (1.0 - x));

    vec3 hot  = vec3(1.0, 0.97, 0.93);
    vec3 mid  = vec3(0.32, 0.78, 1.0);
    vec3 cool = mix(vec3(0.10, 0.30, 1.0), vec3(0.42, 0.24, 1.0), uBoost);
    vec3 cc = mix(hot, mid, smoothstep(0.04, 0.40 + 0.40 * uBoost, x));
    vec3 gc = mix(mid, cool, smoothstep(0.0, 0.85, x));
    float edge = 1.0 - smoothstep(0.8, 1.0, abs(y));
    float tail = 1.0 - smoothstep(0.92, 1.0, x);
    return (cc * core * 7.0 + gc * glow * 1.4) * edge * tail;
}

void main(){
    vec2 uv = vUv;
    vec3 c;
    if (vMode == 0) {
        float y = uv.y;
        float a = exp(-y * y * 4.0) * 0.6 + exp(-y * y * 30.0) * 0.7;
        a *= 1.0 - smoothstep(0.85, 1.0, abs(y));
        c = vCol.rgb * a;
    } else if (vMode == 1) {
        c = plume(uv) * vCol.rgb;
    } else if (vMode == 2) {
        float r2 = dot(uv, uv);
        float a = exp(-r2 * 7.0) + 0.3 * exp(-r2 * 2.2);
        a *= 1.0 - smoothstep(0.5, 1.0, r2);
        c = vCol.rgb * a;
    } else if (vMode == 3) {
        float r = length(uv);
        float k1 = (r - 0.86) / 0.05;
        float k2 = (r - 0.70) / 0.22;
        float a = exp(-k1 * k1) + 0.12 * exp(-k2 * k2);
        a *= 1.0 - smoothstep(0.94, 1.0, r);
        c = vCol.rgb * a;
    } else if (vMode == 4) {
        float y = uv.y;
        float a = exp(-y * y * 3.0) * (1.0 - smoothstep(0.8, 1.0, abs(y)));
        a *= pow(1.0 - clamp(uv.x, 0.0, 1.0), 1.4);
        c = vCol.rgb * a;
    } else {
        float a = exp(-uv.y * uv.y * 5.0) * pow(max(1.0 - abs(uv.x), 0.0), 2.5);
        a *= 1.0 - smoothstep(0.75, 1.0, abs(uv.y));
        c = vCol.rgb * a;
    }
    c *= vCol.a;
    FragColor = vec4(max(c, vec3(0.0)), 0.0);
}

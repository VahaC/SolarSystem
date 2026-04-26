#version 330 core
in vec3 vWorldPos; in vec3 vNormal; in vec2 vUv;
out vec4 fragColor;
uniform sampler2D uTex; uniform vec3 uColor;
// V12: animated surface granulation. uCorona=0 keeps the Sun a static texture.
uniform float uTime;
uniform int uCorona;

float hash13(vec3 p) {
    p = fract(p * 0.3183099 + vec3(0.71, 0.113, 0.419));
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float vnoise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(i + vec3(0.0,0.0,0.0));
    float n100 = hash13(i + vec3(1.0,0.0,0.0));
    float n010 = hash13(i + vec3(0.0,1.0,0.0));
    float n110 = hash13(i + vec3(1.0,1.0,0.0));
    float n001 = hash13(i + vec3(0.0,0.0,1.0));
    float n101 = hash13(i + vec3(1.0,0.0,1.0));
    float n011 = hash13(i + vec3(0.0,1.0,1.0));
    float n111 = hash13(i + vec3(1.0,1.0,1.0));
    return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
               mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}
float fbm(vec3 p) {
    float a = 0.5, sum = 0.0;
    for (int i = 0; i < 4; i++) { sum += a * vnoise(p); p *= 2.07; a *= 0.5; }
    return sum;
}

void main(){
    vec3 base = texture(uTex, vUv).rgb;
    vec3 col = base * uColor;
    if (uCorona != 0) {
        // 3D fbm on the world-space sphere normal — drifts slowly in time so
        // hot granules ripple across the disc without ever lining up with the
        // texture seam at uv.x = 0.
        vec3 q = normalize(vNormal) * 4.0 + vec3(0.0, uTime * 0.04, 0.0);
        float n = fbm(q);
        // Slow heartbeat-like pulse + per-granule jitter.
        float pulse = 0.85 + 0.45 * n + 0.15 * sin(uTime * 0.7 + n * 9.0);
        // Warm tint where it's hottest, cooler in the lanes between granules.
        vec3 hot  = vec3(1.10, 0.82, 0.45);
        vec3 cool = vec3(0.80, 0.45, 0.20);
        vec3 tint = mix(cool, hot, smoothstep(0.35, 0.75, n));
        col *= pulse * tint;
    }
    // HDR output: deliberately exceeds 1.0 so the bright-pass post-process
    // picks the Sun up as a bloom source.
    fragColor = vec4(col * 2.6, 1.0);
}

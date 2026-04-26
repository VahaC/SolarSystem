#version 330 core
in vec3 vWorldPos; in vec3 vNormal; in vec2 vUv;
out vec4 fragColor;
uniform sampler2D uTex;
uniform sampler2D uNightTex;
uniform sampler2D uRingTex;
uniform int uHasNight;
uniform int uHasRing;
uniform float uRingInner;
uniform float uRingOuter;
uniform vec3 uRingNormal;
uniform vec3 uLightPos; uniform vec3 uViewPos; uniform vec3 uLightColor; uniform vec3 uTint;
uniform vec3 uPlanetCenter;
void main(){
    // Sphere normal from world geometry (independent of model-matrix conventions).
    vec3 N = normalize(vWorldPos - uPlanetCenter);
    vec3 L = normalize(uLightPos - vWorldPos);
    vec3 V = normalize(uViewPos - vWorldPos);
    vec3 R = reflect(-L, N);
    float NdotL = dot(N, L);
    float diff = max(NdotL, 0.0);
    float spec = pow(max(dot(V,R), 0.0), 24.0) * 0.15;
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
    vec3 lit = base * uLightColor * diff * ringShadow;
    vec3 color = ambient + lit + uLightColor * spec * diff * ringShadow;
    // V4: night-side emissive map (city lights). Only contributes on the dark
    // side of the terminator and fades smoothly across it.
    if (uHasNight != 0) {
        vec3 night = texture(uNightTex, vUv).rgb;
        float nightMix = 1.0 - smoothstep(-0.05, 0.2, NdotL);
        color += night * nightMix;
    }
    fragColor = vec4(color, 1.0);
}

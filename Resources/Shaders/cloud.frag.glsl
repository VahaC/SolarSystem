#version 330 core
in vec3 vWorldPos; in vec3 vNormal; in vec2 vUv;
out vec4 fragColor;
uniform sampler2D uTex;
uniform vec3 uPlanetCenter;
uniform vec3 uLightPos;
uniform vec3 uLightColor;
void main(){
    vec3 N = normalize(vWorldPos - uPlanetCenter);
    vec3 L = normalize(uLightPos - vWorldPos);
    float NdotL = max(dot(N, L), 0.0);
    vec3 c = texture(uTex, vUv).rgb;
    float lum = max(c.r, max(c.g, c.b));
    float a = smoothstep(0.18, 0.92, lum);
    vec3 lit = c * uLightColor * (0.10 + 0.90 * NdotL);
    fragColor = vec4(lit, a);
}

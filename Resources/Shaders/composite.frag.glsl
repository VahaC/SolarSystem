#version 330 core
in vec2 vUv; out vec4 fragColor;
uniform sampler2D uScene;
uniform sampler2D uBloom;
uniform float uBloomStrength;
// V10: tone mapping + exposure.
uniform float uExposure;
uniform int uAutoExposure;

// ACES filmic tone-mapping curve (Krzysztof Narkowicz's fit). Maps unbounded
// HDR linear input into [0,1] LDR output with a pleasant filmic shoulder.
vec3 ACESFilm(vec3 x) {
    const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main(){
    vec3 hdr   = texture(uScene, vUv).rgb;
    vec3 bloom = texture(uBloom, vUv).rgb;
    vec3 c = hdr + bloom * uBloomStrength;
    float exposure = uExposure;
    if (uAutoExposure != 0) {
        // The 1x1 mip of the HDR scene is the average colour; aim for a
        // middle-grey luminance of 0.18 and clamp the correction so deep-space
        // frames don't blow out the Sun on the next composite.
        vec3 avg = textureLod(uScene, vec2(0.5), 20.0).rgb;
        float lum = dot(avg, vec3(0.2126, 0.7152, 0.0722));
        exposure *= clamp(0.18 / max(lum, 1e-3), 0.25, 4.0);
    }
    fragColor = vec4(ACESFilm(c * exposure), 1.0);
}

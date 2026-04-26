#version 330 core
// V11: Compact FXAA 3.x-style edge anti-aliasing. Operates on the LDR
// post-tonemap image; finds bright/dark edges via 5-tap luma neighbourhood
// and blurs along them in screen space.
in vec2 vUv;
out vec4 fragColor;
uniform sampler2D uTex;
uniform vec2 uTexel; // 1.0 / viewport size

float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

void main() {
    vec3 rgbM  = texture(uTex, vUv).rgb;
    vec3 rgbNW = textureOffset(uTex, vUv, ivec2(-1, -1)).rgb;
    vec3 rgbNE = textureOffset(uTex, vUv, ivec2( 1, -1)).rgb;
    vec3 rgbSW = textureOffset(uTex, vUv, ivec2(-1,  1)).rgb;
    vec3 rgbSE = textureOffset(uTex, vUv, ivec2( 1,  1)).rgb;
    float lumaM  = luma(rgbM);
    float lumaNW = luma(rgbNW);
    float lumaNE = luma(rgbNE);
    float lumaSW = luma(rgbSW);
    float lumaSE = luma(rgbSE);
    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
    float range = lumaMax - lumaMin;
    // Local contrast below threshold => no edge, output centre tap untouched.
    if (range < max(0.0312, lumaMax * 0.125)) {
        fragColor = vec4(rgbM, 1.0);
        return;
    }
    vec2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));
    float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.25 * 0.125, 1.0 / 128.0);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    dir = clamp(dir * rcpDirMin, vec2(-8.0), vec2(8.0)) * uTexel;
    vec3 rgbA = 0.5 * (
        texture(uTex, vUv + dir * (1.0 / 3.0 - 0.5)).rgb +
        texture(uTex, vUv + dir * (2.0 / 3.0 - 0.5)).rgb);
    vec3 rgbB = rgbA * 0.5 + 0.25 * (
        texture(uTex, vUv + dir * -0.5).rgb +
        texture(uTex, vUv + dir *  0.5).rgb);
    float lumaB = luma(rgbB);
    if (lumaB < lumaMin || lumaB > lumaMax) fragColor = vec4(rgbA, 1.0);
    else fragColor = vec4(rgbB, 1.0);
}

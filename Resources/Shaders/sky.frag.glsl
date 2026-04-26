#version 330 core
in vec2 vNdc; out vec4 fragColor;
uniform sampler2D uTex;
uniform mat4 uInvViewProj;
// R3: adaptive star brightness / saturation. uBrightness scales the final colour
// (faded in deep space, restored close to the Sun); uSaturation interpolates
// between greyscale (1.0 = original colour, <1 desaturate, >1 punch up).
uniform float uBrightness;
uniform float uSaturation;
void main(){
    // Reconstruct world-space direction from clip-space. With camera translation
    // stripped from the view matrix, the camera sits at the world origin for the sky
    // pass, so the resulting world point IS the view direction.
    vec4 wp = uInvViewProj * vec4(vNdc, 1.0, 1.0);
    vec3 dir = normalize(wp.xyz / wp.w);
    // Equirectangular UV: longitude from atan2(z, x), latitude from asin(y).
    float u = atan(dir.z, dir.x) / 6.2831853 + 0.5;
    float v = asin(clamp(dir.y, -1.0, 1.0)) / 3.1415926 + 0.5;
    vec3 c = texture(uTex, vec2(u, 1.0 - v)).rgb;
    float lum = dot(c, vec3(0.299, 0.587, 0.114));
    c = mix(vec3(lum), c, uSaturation);
    fragColor = vec4(c * uBrightness, 1.0);
}

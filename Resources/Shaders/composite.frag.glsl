#version 330 core
in vec2 vUv; out vec4 fragColor;
uniform sampler2D uScene;
uniform sampler2D uBloom;
uniform float uBloomStrength;
void main(){
    vec3 hdr   = texture(uScene, vUv).rgb;
    vec3 bloom = texture(uBloom, vUv).rgb;
    fragColor = vec4(hdr + bloom * uBloomStrength, 1.0);
}

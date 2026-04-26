#version 330 core
in vec3 vWorldPos; in vec3 vNormal; in vec2 vUv;
out vec4 fragColor;
uniform sampler2D uTex; uniform vec3 uColor;
void main(){
    vec3 base = texture(uTex, vUv).rgb;
    // HDR output: deliberately exceeds 1.0 so the bright-pass post-process
    // picks the Sun up as a bloom source.
    fragColor = vec4(base * uColor * 2.6, 1.0);
}

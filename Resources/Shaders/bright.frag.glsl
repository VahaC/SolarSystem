#version 330 core
in vec2 vUv; out vec4 fragColor;
uniform sampler2D uTex; uniform float uThreshold;
void main(){
    vec3 c = texture(uTex, vUv).rgb;
    float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
    float k = max(l - uThreshold, 0.0) / max(l, 1e-4);
    fragColor = vec4(c * k, 1.0);
}

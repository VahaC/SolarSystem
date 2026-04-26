#version 330 core
in vec2 vUv; out vec4 fragColor; uniform vec3 uColor;
void main(){
    float d = length(vUv);
    float a = pow(max(0.0, 1.0 - d), 2.5);
    // HDR output: pushed past 1.0 in the core so the bloom pass ignites a halo.
    fragColor = vec4(uColor * a * 2.0, a);
}

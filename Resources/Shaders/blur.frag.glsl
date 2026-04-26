#version 330 core
in vec2 vUv; out vec4 fragColor;
uniform sampler2D uTex; uniform vec2 uTexel;
void main(){
    float w0 = 0.227027;
    float w1 = 0.1945946;
    float w2 = 0.1216216;
    float w3 = 0.054054;
    float w4 = 0.016216;
    vec3 c = texture(uTex, vUv).rgb * w0;
    c += texture(uTex, vUv + uTexel * 1.0).rgb * w1;
    c += texture(uTex, vUv - uTexel * 1.0).rgb * w1;
    c += texture(uTex, vUv + uTexel * 2.0).rgb * w2;
    c += texture(uTex, vUv - uTexel * 2.0).rgb * w2;
    c += texture(uTex, vUv + uTexel * 3.0).rgb * w3;
    c += texture(uTex, vUv - uTexel * 3.0).rgb * w3;
    c += texture(uTex, vUv + uTexel * 4.0).rgb * w4;
    c += texture(uTex, vUv - uTexel * 4.0).rgb * w4;
    fragColor = vec4(c, 1.0);
}

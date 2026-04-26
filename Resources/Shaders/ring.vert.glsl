#version 330 core
layout(location=0) in vec3 aPos; layout(location=1) in vec2 aUv;
uniform mat4 uModel; uniform mat4 uView; uniform mat4 uProj;
uniform float uFcoef;
out vec2 vUv;
out vec3 vWorldPos;
void main(){
    vUv = aUv;
    vec4 wp = uModel * vec4(aPos, 1.0);
    vWorldPos = wp.xyz;
    gl_Position = uProj * uView * wp;
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
}

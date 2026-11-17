#version 330 core
// PROTOTYPE (2026-11-17): arcade flight mode — the player's ship (vertex stage).
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in float aMat;
uniform mat4 uModel; uniform mat4 uView; uniform mat4 uProj;
uniform float uFcoef;
out vec3 vWorldPos;
out vec3 vNormal;
out vec3 vLocalPos;
out vec3 vLocalNormal;
flat out int vMat;
void main(){
    vec4 wp = uModel * vec4(aPos, 1.0);
    vWorldPos = wp.xyz;
    vNormal = mat3(uModel) * aNormal;   // uniform scale: no inverse-transpose needed
    vLocalPos = aPos;
    vLocalNormal = aNormal;
    vMat = int(aMat + 0.5);
    gl_Position = uProj * uView * wp;
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
}

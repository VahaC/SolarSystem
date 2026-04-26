#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in float aAge;
uniform mat4 uView; uniform mat4 uProj;
uniform float uFcoef;
out float vAge;
void main(){
    vAge = aAge;
    gl_Position = uProj * uView * vec4(aPos, 1.0);
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
}

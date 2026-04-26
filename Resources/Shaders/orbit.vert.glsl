#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uModel; uniform mat4 uView; uniform mat4 uProj;
uniform float uFcoef;
void main(){
    gl_Position = uProj * uView * uModel * vec4(aPos,1.0);
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
}

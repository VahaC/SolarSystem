#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uView; uniform mat4 uProj;
void main(){
    vec4 clip = uProj * uView * vec4(aPos, 1.0);
    clip.z = clip.w;
    gl_Position = clip;
}

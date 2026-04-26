#version 330 core
in float vB; out vec4 fragColor;
void main(){ fragColor = vec4(vec3(vB), 1.0); }

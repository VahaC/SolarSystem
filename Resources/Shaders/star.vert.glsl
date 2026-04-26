#version 330 core
layout(location=0) in vec3 aPos; layout(location=1) in float aBrightness;
uniform mat4 uView; uniform mat4 uProj;
out float vB;
void main(){ gl_Position = uProj * uView * vec4(aPos,1.0); gl_PointSize = 1.5 + aBrightness*1.5; vB = aBrightness; }

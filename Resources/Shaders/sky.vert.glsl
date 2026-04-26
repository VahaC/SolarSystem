#version 330 core
layout(location=0) in vec2 aPos;
layout(location=1) in vec2 aUv;
out vec2 vNdc;
void main(){
    vNdc = aPos;                       // _quadVao positions are already in NDC (-1..1)
    gl_Position = vec4(aPos, 1.0, 1.0); // far plane
}

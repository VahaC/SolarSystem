#version 330 core
in float vAge; out vec4 fragColor; uniform vec4 uColor;
void main(){
    // Quadratic falloff so most of the trail is faint and only the head is bright.
    float a = uColor.a * vAge * vAge;
    fragColor = vec4(uColor.rgb, a);
}

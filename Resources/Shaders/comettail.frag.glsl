#version 330 core
in vec2 vCorner; in float vLife; out vec4 fragColor;
void main(){
    float r = length(vCorner);
    if (r > 1.0) discard;
    float a = (1.0 - r);
    a = a * a * vLife;
    // Ion-tail blue at birth fading to a cooler dust-grey as particles age.
    vec3 c = mix(vec3(0.6, 0.7, 0.85), vec3(0.55, 0.85, 1.15), vLife);
    fragColor = vec4(c, a);
}

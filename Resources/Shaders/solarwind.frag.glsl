#version 330 core
in vec2 vCorner; in float vLife; out vec4 fragColor;
void main(){
    float r = length(vCorner);
    if (r > 1.0) discard;
    float a = (1.0 - r);
    a = a * a * vLife;
    // Colour shifts from cool yellow at birth to a faint orange-red as it ages.
    vec3 c = mix(vec3(1.0, 0.45, 0.15), vec3(1.0, 0.95, 0.55), vLife);
    fragColor = vec4(c, a);
}

#version 330 core
in vec2 vCorner; in float vLife; out vec4 fragColor;
void main(){
    float r = length(vCorner);
    if (r > 1.0) discard;
    float falloff = 1.0 - r;
    falloff = falloff * falloff;
    // White-hot core when newborn -> bright orange -> deep red as it cools.
    vec3 hot   = vec3(1.0, 0.95, 0.75);
    vec3 mid   = vec3(1.0, 0.55, 0.18);
    vec3 cool  = vec3(0.65, 0.10, 0.04);
    vec3 c = mix(cool, mix(mid, hot, smoothstep(0.5, 1.0, vLife)),
                       smoothstep(0.0, 0.6, vLife));
    float a = falloff * (0.35 + 0.65 * vLife);
    fragColor = vec4(c, a);
}

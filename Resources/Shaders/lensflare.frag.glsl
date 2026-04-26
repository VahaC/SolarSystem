#version 330 core
in vec2 vUv; out vec4 fragColor;
uniform vec2 uSunNdc;
uniform float uIntensity;
uniform float uAspect;
void main(){
    vec2 ndc = vUv * 2.0 - 1.0;
    vec2 dir = -uSunNdc; // sun -> screen centre
    float fracs[6] = float[](0.0, 0.4, 0.7, 1.0, 1.4, 1.8);
    float sizes[6] = float[](0.10, 0.05, 0.04, 0.06, 0.03, 0.05);
    vec3 tints[6] = vec3[](
        vec3(1.00, 0.85, 0.55),
        vec3(0.55, 0.75, 1.00),
        vec3(1.00, 0.45, 0.45),
        vec3(0.65, 1.00, 0.55),
        vec3(1.00, 0.95, 0.65),
        vec3(0.85, 0.60, 1.00)
    );
    vec3 col = vec3(0.0);
    for (int i = 0; i < 6; i++) {
        vec2 c = uSunNdc + dir * fracs[i];
        vec2 d = ndc - c;
        d.x *= uAspect;
        float r = length(d);
        float v = smoothstep(sizes[i], 0.0, r);
        col += tints[i] * v;
    }
    fragColor = vec4(col * uIntensity, 1.0);
}

#version 330 core
in vec2 vUv; in vec3 vWorldPos; out vec4 fragColor;
uniform sampler2D uTex;
uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform vec3 uLightPos;
uniform int uHasShadow;
void main(){
    vec4 c = texture(uTex, vUv);
    // V5: Saturn-on-ring shadow. Ray from the ring fragment toward the Sun
    // intersected with Saturn's bounding sphere; if the segment hits the sphere
    // (t in (0, 1)) the fragment is occluded and we darken it to a soft shadow.
    if (uHasShadow != 0) {
        vec3 d = uLightPos - vWorldPos;
        vec3 oc = vWorldPos - uPlanetCenter;
        float a = dot(d, d);
        float b = dot(oc, d);
        float cc = dot(oc, oc) - uPlanetRadius * uPlanetRadius;
        float disc = b * b - a * cc;
        if (disc > 0.0) {
            float sq = sqrt(disc);
            float t0 = (-b - sq) / a;
            float t1 = (-b + sq) / a;
            if ((t0 > 0.0 && t0 < 1.0) || (t1 > 0.0 && t1 < 1.0))
                c.rgb *= 0.25;
        }
    }
    fragColor = c;
}

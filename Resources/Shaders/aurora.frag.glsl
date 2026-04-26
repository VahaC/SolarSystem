#version 330 core
// V13: aurora ribbon fragment shader. Top edge (vSide=0) glows brightly, bottom
// edge fades to zero alpha; an angular shimmer on top of that animates the
// curtain. Output is HDR-ish so the bloom pass picks up the brightest crests.
in float vAngle;
in float vSide;
out vec4 fragColor;
uniform vec4 uColor;
uniform float uTime;
uniform float uIntensity;
void main(){
    // Vertical fade across the ribbon.
    float fade = 1.0 - vSide;
    // Angular shimmer (a few harmonics so it doesn't look like a single sine).
    float shimmer = 0.55
                  + 0.30 * sin(vAngle * 9.0 + uTime * 1.2)
                  + 0.15 * sin(vAngle * 23.0 - uTime * 2.1);
    float a = max(fade * shimmer, 0.0);
    fragColor = vec4(uColor.rgb * (1.0 + 0.6 * fade), a * uColor.a * uIntensity);
}

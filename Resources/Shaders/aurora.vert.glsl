#version 330 core
// V13: aurora ribbon. Per-vertex inputs are an angle around the magnetic pole
// (vAngle in [0, 2*pi]) and a side selector (vSide = 0 = inner/top edge,
// vSide = 1 = outer/bottom edge). Position is reconstructed on the unit sphere
// then scaled / tilted / translated by uModel into the host planet's frame.
layout(location=0) in float aAngle;
layout(location=1) in float aSide;
uniform mat4 uModel; uniform mat4 uView; uniform mat4 uProj;
uniform float uFcoef;
uniform float uTime;
uniform float uHemisphere; // +1 = north pole, -1 = south pole
out float vAngle;
out float vSide;
void main(){
    // Latitude band: 72° (top) to 80° (bottom), measured from the rotation axis.
    float latTop = radians(72.0);
    float latBot = radians(80.0);
    float lat = mix(latTop, latBot, aSide);
    // A small "curtain" displacement of the bottom edge so the ribbon ripples.
    float wave = sin(aAngle * 6.0 + uTime * 0.8) * 0.030
               + sin(aAngle * 13.0 - uTime * 1.3) * 0.012;
    lat += wave * (1.0 - aSide);
    float s = sin(lat), c = cos(lat);
    vec3 pos = vec3(s * cos(aAngle), c * uHemisphere, s * sin(aAngle));
    vec4 wp = uModel * vec4(pos, 1.0);
    gl_Position = uProj * uView * wp;
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
    vAngle = aAngle;
    vSide = aSide;
}

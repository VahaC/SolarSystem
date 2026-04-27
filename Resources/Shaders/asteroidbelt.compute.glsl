#version 430 core

// A8: GPU Kepler-solve for the asteroid belt. One invocation per asteroid;
// reads per-body Keplerian elements from binding=0 SSBO, writes the packed
// (pos.xyz, brightness) instance record into the same VBO the rasteriser
// already consumes (binding=1 SSBO). Mirrors AsteroidBelt.Update on the CPU
// -- so toggling the GPU path on/off is observation-only.

layout(local_size_x = 64) in;

struct Asteroid {
    // x=A (semi-major axis, AU), y=e, z=n (mean motion rad/day), w=M0 (rad)
    vec4 ae_n_m0;
    // xyz = perifocal X axis (cos w * P + sin w * Q) in GL world; w = brightness (0.4..1.0)
    vec4 ax_b;
    // xyz = perifocal Y axis (-sin w * P + cos w * Q) in GL world; w = sqrt(1-e^2)
    vec4 by_e;
};

layout(std430, binding = 0) readonly  buffer ElementsBlock { Asteroid asteroids[];  };
layout(std430, binding = 1) writeonly buffer OutputBlock   { vec4     outPos[];     };

uniform float uSimDays;
uniform int   uCount;
uniform int   uRealScale;   // 0 = compressed (K * a^(power-1)), 1 = real-scale (uAuToWorld)
uniform float uK;           // 200.0 / 30.07^0.45
uniform float uPower;       // 0.45
uniform float uAuToWorld;   // 50.0

const float TAU = 6.28318530717958647692;

void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(uCount)) return;

    Asteroid a = asteroids[i];
    float A      = a.ae_n_m0.x;
    float e      = a.ae_n_m0.y;
    float n      = a.ae_n_m0.z;
    float M0     = a.ae_n_m0.w;
    float bright = a.ax_b.w;
    float eFac   = a.by_e.w;
    vec3  Ax     = a.ax_b.xyz;
    vec3  Bx     = a.by_e.xyz;

    float M = mod(M0 + n * uSimDays, TAU);
    if (M < 0.0) M += TAU;

    // Newton-Raphson Kepler solve (matches the CPU 6-iteration cap).
    float E = (e < 0.8) ? M : 3.14159265359;
    for (int k = 0; k < 6; ++k) {
        float f  = E - e * sin(E) - M;
        float fp = 1.0 - e * cos(E);
        E -= f / fp;
    }

    float xp = A * (cos(E) - e);
    float yp = A * eFac * sin(E);

    float s = (uRealScale == 1) ? uAuToWorld : (uK * pow(A, uPower - 1.0));
    vec3  pos = (xp * Ax + yp * Bx) * s;

    outPos[i] = vec4(pos, bright);
}

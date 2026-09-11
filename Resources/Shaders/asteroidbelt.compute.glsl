#version 430 core

// A8: GPU Kepler-solve for the asteroid belt. One invocation per asteroid;
// reads per-body Keplerian elements from binding=0 SSBO, writes the packed
// (pos.xyz, brightness) instance record into the same VBO the rasteriser
// already consumes (binding=1 SSBO). Mirrors AsteroidBelt.Update on the CPU
// -- so toggling the GPU path on/off is observation-only.
//
// Physics sandbox (uMode == 1): the same kernel becomes a test-particle
// leapfrog. Each asteroid keeps its barycentric (pos, vel) in the binding=2
// SSBO between frames and replays, step by step, the exact positions of the
// Sun + planets that PhysicsWorld recorded while it integrated this frame
// (binding=3 SSBO). Same force law (G, mass scales, exponent) as the planets,
// so the belt reacts to every constant the user drags.

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
// Physics mode: barycentric state, 2 vec4 per asteroid: (pos.xyz, 0), (vel.xyz, 0).
layout(std430, binding = 2) buffer StateBlock { vec4 state[]; };
// Physics mode: (uStepCount + 1) boundary records, each uStride vec4s:
//   [0]      = (dt to reach this boundary, 0, 0, 0)   (0 for the first record)
//   [1..N]   = (body position xyz, G*M of that body)
layout(std430, binding = 3) readonly buffer StepsBlock { vec4 steps[]; };

uniform float uSimDays;
uniform int   uCount;
uniform int   uRealScale;   // 0 = compressed (K * a^(power-1)), 1 = real-scale (uAuToWorld)
uniform float uK;           // 200.0 / 30.07^0.45
uniform float uPower;       // 0.45
uniform float uAuToWorld;   // 50.0

uniform int   uMode;        // 0 = Kepler solve, 1 = physics replay
uniform int   uBodyCount;   // massive bodies per record (Sun first)
uniform int   uStride;      // vec4s per record = 1 + uBodyCount
uniform int   uStepCount;   // leapfrog steps to replay this frame
uniform float uExponent;    // n in a = GM / r^n
uniform float uMinSep2;     // softening floor (AU^2)

const float TAU = 6.28318530717958647692;

// a = sum_j GM_j (r_j - r) / |r_j - r|^(n+1), record `rec`.
vec3 accel(vec3 r, int rec) {
    vec3 a = vec3(0.0);
    int base = rec * uStride + 1;
    for (int j = 0; j < uBodyCount; ++j) {
        vec4 b = steps[base + j];
        vec3 d = b.xyz - r;
        float d2 = max(dot(d, d), uMinSep2);
        float inv = (uExponent == 2.0) ? (1.0 / (d2 * sqrt(d2))) : pow(d2, -0.5 * (uExponent + 1.0));
        a += d * (b.w * inv);
    }
    return a;
}

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

    // Per-orbit uniform scale (same rule as the planets), keyed on the asteroid's
    // original semi-major axis so switching modes never makes the belt jump.
    float s = (uRealScale == 1) ? uAuToWorld : (uK * pow(A, uPower - 1.0));

    if (uMode == 1) {
        vec3 pos = state[2u * i].xyz;
        vec3 vel = state[2u * i + 1u].xyz;
        vec3 acc = accel(pos, 0);
        for (int k = 1; k <= uStepCount; ++k) {
            float dt = steps[k * uStride].x;
            vel += acc * (0.5 * dt);
            pos += vel * dt;
            acc = accel(pos, k);
            vel += acc * (0.5 * dt);
        }
        state[2u * i]      = vec4(pos, 0.0);
        state[2u * i + 1u] = vec4(vel, 0.0);
        // Heliocentric for rendering: subtract the Sun's final position.
        vec3 sun = steps[uStepCount * uStride + 1].xyz;
        outPos[i] = vec4((pos - sun) * s, bright);
        return;
    }

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

    vec3  pos = (xp * Ax + yp * Bx) * s;

    outPos[i] = vec4(pos, bright);
}

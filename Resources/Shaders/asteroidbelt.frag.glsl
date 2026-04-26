#version 330 core
in vec2 vCorner; in float vLife; out vec4 fragColor;
void main(){
    float r = length(vCorner);
    if (r > 1.0) discard;
    // Asteroids ignore vLife as a fade factor — vLife is reused as a per-asteroid
    // brightness multiplier baked at construction time so each rock keeps a stable
    // apparent magnitude across frames.
    fragColor = vec4(vec3(0.78, 0.72, 0.62) * vLife, vLife);
}

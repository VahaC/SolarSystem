#version 330 core
in vec2 vUv; out vec4 fragColor; uniform sampler2D uTex; uniform vec4 uColor;
void main(){
    // Atlas is RGBA8 with white opaque pixels for ink and transparent elsewhere,
    // so the alpha channel is the glyph mask.
    float a = texture(uTex, vUv).a;
    if (a < 0.05) discard;
    fragColor = vec4(uColor.rgb, uColor.a * a);
}

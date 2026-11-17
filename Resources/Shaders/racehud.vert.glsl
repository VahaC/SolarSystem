#version 330 core
// PROTOTYPE (2026-11-17): arcade flight mode - gate race: flat 2D shapes of the race HUD.
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec4 aColor;
uniform mat4 uProj;
out vec4 vColor;
void main()
{
    vColor = aColor;
    gl_Position = uProj * vec4(aPos, 0.0, 1.0);
}

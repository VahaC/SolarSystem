#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUv;
uniform mat4 uModel; uniform mat4 uView; uniform mat4 uProj;
uniform vec3 uPlanetCenter;
uniform float uPlanetRadius;
uniform vec2 uViewportSize;
uniform float uMinPixelRadius;
uniform float uFcoef;
out vec3 vWorldPos; out vec3 vNormal; out vec2 vUv;
void main(){
    vec4 wp = uModel * vec4(aPos,1.0);

    // Minimum screen-space size: if the body's projected radius is smaller than
    // uMinPixelRadius pixels, expand vertex offsets outward from uPlanetCenter so
    // the silhouette never collapses to a single pixel. In real-scale mode this is
    // what keeps the planets visible even from astronomical distances. The radial
    // outward expansion preserves the world-space normal computed in the FS as
    // normalize(vWorldPos - uPlanetCenter), so lighting remains correct.
    if (uMinPixelRadius > 0.0 && uPlanetRadius > 0.0) {
        vec4 cView = uView * vec4(uPlanetCenter, 1.0);
        float zd = max(-cView.z, 1e-4);
        float pxRadius = uPlanetRadius * uProj[1][1] / zd * 0.5 * uViewportSize.y;
        float k = max(1.0, uMinPixelRadius / max(pxRadius, 1e-4));
        wp.xyz = uPlanetCenter + (wp.xyz - uPlanetCenter) * k;
    }

    vWorldPos = wp.xyz;
    // OpenTK uses row-vector matrices, so uploaded uModel is the transpose of the
    // mathematical column-vector matrix. Use inverse-transpose to get the proper
    // normal matrix that rotates the normal in the same direction as the position.
    mat3 normalMat = transpose(inverse(mat3(uModel)));
    vNormal = normalMat * aNormal;
    vUv = aUv;
    gl_Position = uProj * uView * wp;
    // Logarithmic depth: remaps gl_Position.z so depth precision is distributed
    // logarithmically over the [near, far] range, eliminating z-fighting at the huge
    // dynamic range needed for real-scale (R) mode. Fcoef = 2 / log2(far + 1).
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
}

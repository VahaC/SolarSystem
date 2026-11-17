#version 330 core
in vec2 vUv; out vec4 fragColor;
uniform sampler2D uScene;
uniform sampler2D uBloom;
uniform float uBloomStrength;
// V10: tone mapping + exposure.
uniform float uExposure;
uniform int uAutoExposure;
// Flight post-processing. x = strength (0 outside flight mode, 1 in it),
// y = ship speed 0..1 (cruise ~0.25), z = boost 0..1 (smoothed), w = time (s).
uniform vec4 uFlightPost;
// UV point the ship is flying toward: the centre of the speed effects.
uniform vec2 uFlightCenter;
uniform vec2 uResolution;

// ACES filmic tone-mapping curve (Krzysztof Narkowicz's fit). Maps unbounded
// HDR linear input into [0,1] LDR output with a pleasant filmic shoulder.
vec3 ACESFilm(vec3 x) {
    const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

// ---------------------------------------------------------------------------
// Flight-mode helpers (only reached when uFlightPost.x > 0).
// ---------------------------------------------------------------------------
const int BLUR_TAPS = 10;

// HDR scene + bloom at one UV, exactly as the plain path combines them.
vec3 sceneAt(vec2 uv) {
    // Explicit LOD 0: uScene is mipmapped (auto-exposure) and these fetches sit
    // in per-pixel control flow, where implicit derivatives are undefined.
    return textureLod(uScene, uv, 0.0).rgb + textureLod(uBloom, uv, 0.0).rgb * uBloomStrength;
}

// Interleaved gradient noise (Jimenez): cheap per-pixel dither in [0,1).
float ign(vec2 p) {
    return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715))));
}

// Hash for film grain, in [0,1).
float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float luma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

vec3 flightComposite(float exposure) {
    float s     = clamp(uFlightPost.x, 0.0, 1.0);
    float speed = clamp(uFlightPost.y, 0.0, 1.0);
    float boost = clamp(uFlightPost.z, 0.0, 1.0);
    float t     = uFlightPost.w;
    float aspect = uResolution.x / max(uResolution.y, 1.0);

    // Radial distance from the heading point, aspect-corrected and normalised so
    // a screen corner seen from the screen centre is ~1.
    vec2  d  = vUv - uFlightCenter;
    float rn = length(d * vec2(aspect, 1.0)) / length(vec2(0.5 * aspect, 0.5));

    // Edge mask: 0 around the heading point (the gate ahead) and the ship just
    // below it, ramping up toward the screen edges.
    float edge = smoothstep(0.3, 1.1, rn);
    edge *= edge;

    // --- 1+2. Speed blur, barrel stretch and chromatic aberration ------------
    // All three only exist while boosting; at cruise (boost = 0) this collapses
    // to one plain tap, so the cruise image is pixel-sharp.
    float kick = s * boost;
    // Blur length as a fraction of the distance to the centre (max ~4.4 % at the
    // corners at full boost; speed adds a little on top of boost).
    float blurLen = kick * (0.032 + 0.012 * speed) * edge;
    // Barrel "stretch": at the edges sample slightly closer to the centre, so the
    // periphery is pulled outward as you accelerate.
    float barrel = 1.0 - 0.022 * kick * rn * rn;
    // Lateral chromatic aberration: red sampled further out, blue further in.
    float ca = kick * 0.0022 * edge;

    vec3 hdr;
    if (blurLen + ca < 1e-5) {
        hdr = sceneAt(uFlightCenter + d * barrel);
    } else {
        // Spectral radial blur: taps walk inward from (1 + ca) to (1 - ca - blurLen)
        // of the distance; each tap is weighted red -> green -> blue along the
        // walk, so the same taps give both the streak and the colour fringe.
        float dither = ign(gl_FragCoord.xy + vec2(fract(t * 7.0) * 61.0));
        float span = blurLen + 2.0 * ca;
        vec3 acc = vec3(0.0), wsum = vec3(0.0);
        for (int i = 0; i < BLUR_TAPS; ++i) {
            float k = (float(i) + dither) / float(BLUR_TAPS); // 0..1
            vec2 uv = uFlightCenter + d * barrel * (1.0 + ca - span * k);
            // Smooth spectral weights; all channels share the middle so the blur
            // stays neutral and only the ends tint.
            vec3 w = vec3(1.0 - smoothstep(0.0, 0.7, k),
                          1.0 - abs(2.0 * k - 1.0) * 0.6,
                          smoothstep(0.3, 1.0, k)) + 0.8;
            acc  += sceneAt(uv) * w;
            wsum += w;
        }
        hdr = acc / wsum;
    }

    // --- 3. Grade -------------------------------------------------------------
    // Pre-ACES (scene-linear): a small vibrance lift. Done before tone mapping so
    // it acts on the real chroma of engine blues / gate cyans; ACES' shoulder then
    // keeps the boosted HDR highlights from clipping, and since luminance is
    // preserved the exposure / auto-exposure balance is unchanged.
    hdr = max(hdr, vec3(0.0));
    float L = luma(hdr);
    float sat = (max(hdr.r, max(hdr.g, hdr.b)) - min(hdr.r, min(hdr.g, hdr.b)))
              / max(max(hdr.r, max(hdr.g, hdr.b)), 1e-4);
    hdr = max(vec3(L) + (hdr - vec3(L)) * (1.0 + 0.22 * s * (1.0 - sat)), vec3(0.0));

    // Exposure + ACES exactly as in the plain path.
    vec3 c = ACESFilm(hdr * exposure);

    // Post-ACES (display-referred, bounded 0..1): contrast and split toning. The
    // curve is anchored at 0 and 1, so it cannot blow out the sunlit ring or push
    // the dark side of the ring below black; done after ACES so its strength does
    // not depend on the exposure.
    vec3 sc = c * c * (3.0 - 2.0 * c);              // gentle S-curve
    c = mix(c, sc, 0.14 * s);
    float l = luma(c);
    vec3 coolShadows = vec3(-0.010, 0.002, 0.018) * (1.0 - l) * (1.0 - l);
    vec3 warmHighs   = vec3( 0.016, 0.006, -0.014) * l * l;
    c = clamp(c + (coolShadows + warmHighs) * s, 0.0, 1.0);

    // Vignette around the true screen centre (not the heading point), slightly
    // deeper at boost to tunnel the view.
    vec2 vq = (vUv - 0.5) * vec2(aspect, 1.0) / (0.5 * aspect);
    float vr = length(vq);
    float vig = 1.0 - (0.22 + 0.10 * boost) * s * smoothstep(0.45, 1.25, vr);
    c *= vig;

    // Film grain: very light, mid-tone weighted, re-seeded ~30 times a second.
    float g = hash12(gl_FragCoord.xy + vec2(floor(t * 30.0) * 17.31, 0.0)) - 0.5;
    float amp = (0.016 + 0.014 * boost) * s;
    c += g * amp * (0.25 + 0.75 * sqrt(clamp(luma(c), 0.0, 1.0))) * (1.0 - luma(c) * 0.6);

    return clamp(c, 0.0, 1.0);
}

void main(){
    if (uFlightPost.x <= 0.0) {
        // Plain (sandbox) path: unchanged.
        vec3 hdr   = texture(uScene, vUv).rgb;
        vec3 bloom = texture(uBloom, vUv).rgb;
        vec3 c = hdr + bloom * uBloomStrength;
        float exposure = uExposure;
        if (uAutoExposure != 0) {
            // The 1x1 mip of the HDR scene is the average colour; aim for a
            // middle-grey luminance of 0.18 and clamp the correction so deep-space
            // frames don't blow out the Sun on the next composite.
            vec3 avg = textureLod(uScene, vec2(0.5), 20.0).rgb;
            float lum = dot(avg, vec3(0.2126, 0.7152, 0.0722));
            exposure *= clamp(0.18 / max(lum, 1e-3), 0.25, 4.0);
        }
        fragColor = vec4(ACESFilm(c * exposure), 1.0);
        return;
    }

    // Flight path: same exposure / auto-exposure logic as above.
    float exposure = uExposure;
    if (uAutoExposure != 0) {
        vec3 avg = textureLod(uScene, vec2(0.5), 20.0).rgb;
        float lum = dot(avg, vec3(0.2126, 0.7152, 0.0722));
        exposure *= clamp(0.18 / max(lum, 1e-3), 0.25, 4.0);
    }
    fragColor = vec4(flightComposite(exposure), 1.0);
}

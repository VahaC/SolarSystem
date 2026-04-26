# 🛰️ Roadmap

Ideas for future versions of the Solar System simulation, grouped by impact-vs-effort.
Priorities are subjective — anything here is fair game, in any order.

---

## 🥇 Top picks (highest wow-per-line-of-code)

1. ~~**Bloom / HDR glow on Sun & flares** — fullscreen post-process: extract bright pixels → Gaussian blur → additive composite. Will instantly raise the perceived production value.~~ ✅ **Done** — implemented in `Renderer` as an HDR (RGBA16F) offscreen target + bright-pass + 6-pass separable Gaussian blur + additive composite (`BeginScene` / `EndSceneAndApplyBloom`).
2. **Smooth focus transition + planet trails** — lerp `Camera.Target` and `Distance` over ~0.5 s instead of snapping; render a fading line strip behind each planet for the last *N* positions. Looks "cinematic" especially at high simulation speed.
3. **Logarithmic depth + minimum screen-size dots** — make the real-scale (R) mode actually usable: log-depth shader (`gl_FragDepth = log2(1+w)/log2(1+far)`) eliminates z-fighting at huge near/far ratios, and a per-body screen-space minimum size (e.g. ≥ 2 px) keeps planets visible even from astronomical distances.

---

## 🎨 Visual polish

| # | Feature | Notes |
|---|---|---|
| V1 | Bloom / HDR glow on Sun & particles | ✅ Implemented (RGBA16F FBO + bright-pass + separable Gaussian + additive composite). |
| V2 | Atmospheric rim-light (Earth, Venus, Jupiter, Neptune) | Fresnel `pow(1-dot(N,V), 3)` tinted by atmosphere colour in planet shader. |
| V3 | Earth cloud layer | Second alpha-blended sphere using `8k_earth_clouds.jpg`, slowly counter-rotating. |
| V4 | Earth night-side city lights | `8k_earth_nightmap.jpg`; in shader: `mix(night, day, smoothstep(0, 0.2, NdotL))`. |
| V5 | Saturn ring shadow on the planet (and vice versa) | Ray-vs-disk in planet shader / ray-vs-sphere in ring shader. |
| V6 | Lens flare on the Sun | Screen-space ghosts when the Sun is near the camera forward axis. |
| V7 | Improved Milky Way sky | Actual equirectangular `8k_stars_milkyway.jpg` instead of procedural noise. |

## ⏱️ Simulation features

| # | Feature | Notes |
|---|---|---|
| S1 | Pause (Space) and reverse-time keys (`,` / `.`) | ✅ Done. `Space` toggles pause; `,` plays backward, `.` plays forward (magnitude preserved); `+`/`-` work in both directions. |
| S2 | Planet trails | Ring buffer of last N positions per body, drawn as fading line strip. |
| S3 | Asteroid belt | ~10 k instanced billboards or tiny spheres between Mars and Jupiter, randomised semi-major axes / eccentricities / inclinations within real ranges. |
| S4 | Comet with ion / dust tail | High-eccentricity Kepler orbit; particle tail always pointing away from the Sun. |
| S5 | Date seek | `T` key opens text input; jump to a specific calendar date or `±N` days. |
| S6 | Major moons | Jovian (Io, Europa, Ganymede, Callisto), Saturnian (Titan), reusing the Moon pattern. |
| S7 | Dwarf planets | Pluto, Ceres, Eris, Makemake, Haumea — same Kepler + texture pipeline. |
| S8 | Constellation overlay | Optional toggle that draws constellation lines and names from a JSON data file. |

## 🛠 Quality-of-life

| # | Feature | Notes |
|---|---|---|
| Q1 | Smooth focus transitions | Lerp `Camera.Target` and `Distance` instead of snapping. |
| Q2 | Click-to-pick the Moon | `TryPick` currently iterates only over planets + Sun — extend to `_moon`. |
| Q3 | Search bodies by name | Ctrl+F overlay with autocomplete over `_planets[].Name` (+ Moon, Sun, future bodies). |
| Q4 | Screenshot key | F12 → `GL.ReadPixels` + `System.Drawing` PNG export to `screenshots/`. |
| Q5 | Persisted settings | Save camera/yaw/pitch/distance, sim speed, focus, toggles to `%AppData%/SolarSystem/state.json` on exit and reload on launch. |
| Q6 | Mouse hover tooltip | Tiny tooltip with body name + distance when the cursor hovers over a body. |
| Q7 | FPS / particle-count overlay | Optional debug HUD (`~` key) showing FPS, draw calls, particle counts, scale mode. |

## 🌌 Real-scale mode UX

| # | Feature | Notes |
|---|---|---|
| R1 | Logarithmic depth buffer | Custom `gl_FragDepth` in all geometry shaders to keep depth precision usable across the 10⁷ near/far range needed for true scale. |
| R2 | Screen-space minimum body size | Vertex / geometry shader: if projected radius < 2 px, expand to 2 px so dots stay visible from any distance. |
| R3 | Adaptive star brightness | Make stars fainter when far from Sun (deep space), more saturated near a planet. |
| R4 | "Light-time" visualisation | Optional toggle that delays the Sun's lighting by the actual `r/c` light travel time at each planet. Tiny but cute. |

## 🚀 Architecture / refactor

| # | Item | Notes |
|---|---|---|
| A1 | Cross-platform font fallback | `BitmapFont` is currently `[SupportedOSPlatform("windows")]` (GDI+). Add a SkiaSharp / FreeType backend so it runs on Linux/macOS. |
| A2 | Move shader source to `.glsl` files | Currently embedded as C# strings; move to `Resources/Shaders/*.glsl` with `EmbeddedResource` or `<Content>` items. |
| A3 | Move planet data to `planets.json` | Same J2000 elements, but data-driven so users can add bodies without recompiling. |
| A4 | Replace point-sprite particles with instanced quads | Better cross-driver behaviour; current `gl_PointSize` is capped at small values on some GPUs. |
| A5 | Frame-time-independent particle systems | Use sub-stepped integration so very high `_daysPerSecond` doesn't desync particles. |

---

If you want to claim any of these, open a PR or just start implementing — most items are self-contained and shouldn't conflict with each other.

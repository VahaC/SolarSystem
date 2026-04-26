# 🛰️ Roadmap

Ideas for future versions of the Solar System simulation, grouped by impact-vs-effort.
Priorities are subjective — anything here is fair game, in any order.

---

## 🥇 Top picks (highest wow-per-line-of-code)

1. ~~**Bloom / HDR glow on Sun & flares** — fullscreen post-process: extract bright pixels → Gaussian blur → additive composite. Will instantly raise the perceived production value.~~ ✅ **Done** — implemented in `Renderer` as an HDR (RGBA16F) offscreen target + bright-pass + 6-pass separable Gaussian blur + additive composite (`BeginScene` / `EndSceneAndApplyBloom`).
2. ~~**Smooth focus transition + planet trails** — lerp `Camera.Target` and `Distance` over ~0.5 s instead of snapping; render a fading line strip behind each planet for the last *N* positions. Looks "cinematic" especially at high simulation speed.~~ ✅ **Done** — `SolarSystemWindow.FocusOn` now starts a 0.5 s smoothstep lerp on `Camera.Target`/`Distance` (tracking the body's live position en route); each `Planet` keeps a 200-sample ring buffer that `Renderer.DrawTrails` rasterises as a per-vertex-alpha-fading `LineStrip`. Toggle with `T`.
3. ~~**Logarithmic depth + minimum screen-size dots** — make the real-scale (R) mode actually usable: log-depth shader (`gl_FragDepth = log2(1+w)/log2(1+far)`) eliminates z-fighting at huge near/far ratios, and a per-body screen-space minimum size (e.g. ≥ 2 px) keeps planets visible even from astronomical distances.~~ ✅ **Done** — every 3D shader (`PlanetVS`/`SunVS`, `OrbitVS`, `RingVS`, `GlowVS`, `TrailVS`, `SolarWind`, `SolarFlares`) now writes `gl_Position.z = (log2(1 + w) * Fcoef - 1) * w` with `Fcoef = 2 / log2(far + 1)`, eliminating z-fighting across the 10⁷ near/far ratio of real-scale mode. `PlanetVS` additionally enforces a screen-space minimum silhouette by radially expanding vertices outward from `uPlanetCenter` whenever the body's projected radius drops below `Renderer.MinPixelRadius` (1 px → ~2 px diameter dot), so planets stay visible at any distance.

---

## 🎨 Visual polish

| # | Feature | Notes |
|---|---|---|
| V1 | Bloom / HDR glow on Sun & particles | ✅ Implemented (RGBA16F FBO + bright-pass + separable Gaussian + additive composite). |
| V2 | Atmospheric rim-light (Earth, Venus, Jupiter, Neptune) | Fresnel `pow(1-dot(N,V), 3)` tinted by atmosphere colour in planet shader. |
| V3 | Earth cloud layer | ✅ Done. `Renderer.DrawClouds` rasterises a second sphere at `1.012 × VisualRadius` using a dedicated `CloudFS` (reuses `PlanetVS` so log-depth + min-pixel apply). Alpha is derived from cloud-texture luminance via `smoothstep(0.18, 0.92, lum)` so the JPG source works without a dedicated alpha channel. Drawn alpha-blended after the opaque planet pass with depth-write off. The cloud sphere has its own `Planet.CloudRotationAngleRad`, advanced at `(spinRate − 0.08 rev/sim-day)` so the layer drifts westward relative to the surface. Loads `textures/8k_earth_clouds.jpg` opportunistically — missing file → no clouds. |
| V4 | Earth night-side city lights | ✅ Done. `PlanetFS` now takes an optional `uNightTex` + `uHasNight` flag; on the dark side of the terminator it adds `night × (1 − smoothstep(-0.05, 0.2, NdotL))` to the lit colour, so emissive city lights only appear where direct sun illumination drops off. `Planet.NightTextureId` is populated from `textures/8k_earth_nightmap.jpg` when present (no-op otherwise), and the night sampler is bound on TU1 in `Renderer.DrawPlanet`. |
| V5 | Saturn ring shadow on the planet (and vice versa) | ✅ Done. `PlanetFS` now takes `uHasRing/uRingInner/uRingOuter/uRingNormal/uRingTex`; for fragments on the lit hemisphere of Saturn it casts a ray toward the Sun, intersects the ring plane (normal = `RotZ(tilt)·(0,1,0)`), and if the hit point falls between the inner/outer ring radii samples the ring texture's alpha and attenuates the diffuse + specular terms by `1 - α`. `RingFS` ray-marches the inverse: from each ring fragment toward the Sun it solves a ray-vs-sphere against Saturn (`uPlanetCenter`, `uPlanetRadius`), darkening the fragment to 25% when the segment hits the planet. `RingVS` was extended to output `vWorldPos` and `Renderer.DrawPlanet` now binds the ring texture on TU2 + uploads ring uniforms only for `Saturn`. |
| V6 | Lens flare on the Sun | ✅ Done. New `Renderer.DrawLensFlare` projects the Sun's world position to NDC (skips when behind camera, faded when off-screen), then renders a fullscreen additive quad whose `LensFlareFS` synthesises 6 coloured ghosts at fixed fractions along the sun-through-screen-centre axis with aspect-corrected radii. Intensity is gated by `dot(viewForward, dirToSun)` so the ghosts only ignite when the Sun is roughly looked-at. Drawn after `EndSceneAndApplyBloom` so it composites on top of the bloomed scene without being smeared by the Gaussian blur. |
| V7 | Improved Milky Way sky | ✅ Done. `SkyFS` reconstructs a per-pixel world-space view direction (clip→`uInvViewProj`) and samples `_starsTexture` as an equirectangular map (`atan2(z,x)`/`asin(y)`) on a fullscreen quad at the far plane, so the procedural noise is replaced wholesale by the real Milky Way panorama whenever a texture is present. `Renderer.Initialize` opportunistically loads `textures/8k_stars_milky_way.jpg` (current convention) with a fallback to `textures/8k_stars_milkyway.jpg` (roadmap spelling); a tiny solid-colour procedural texture is kept only as a last-resort fallback when neither file ships. |

## ⏱️ Simulation features

| # | Feature | Notes |
|---|---|---|
| S1 | Pause (Space) and reverse-time keys (`,` / `.`) | ✅ Done. `Space` toggles pause; `,` plays backward, `.` plays forward (magnitude preserved); `+`/`-` work in both directions. |
| S2 | Planet trails | ✅ Done. Per-planet 200-sample ring buffer rendered as a fading `LineStrip` (alpha quadratic in age). Toggle with `T`; auto-clears on direction reverse and scale toggle. |
| S3 | Asteroid belt | ✅ Done. `AsteroidBelt` precomputes per-asteroid Keplerian elements + perifocal→world basis (ecliptic→GL swap folded in) for 8000 rocks; per frame each is advanced by Newton-Raphson Kepler solve and rendered as additively-blended `GL_POINTS` with logarithmic depth. |
| S4 | Comet with ion / dust tail | ✅ Done. `Comet` runs a 1P/Halley-like ellipse (a≈17.83 AU, e≈0.967, i=162°) through the existing `OrbitalMechanics.HeliocentricPosition`, draws its own orbit polyline, and emits a CPU particle tail in a cone around the anti-Sun axis with intensity scaled by 1/r so the tail only “blazes” near perihelion. |
| S5 | Date seek | ✅ Done. `J` opens a top-of-screen prompt; type `YYYY-MM-DD` (any culture-invariant `DateTime.TryParse` format) or a signed delta like `+30` / `-365` and press Enter. `Esc` cancels. After a jump trails are cleared so they don’t draw a stale arc across the new epoch. |
| S6 | Major moons | ✅ Done. New `Moon` wraps a `Planet` body with `(hostIndex, orbitRadiusKm/artistic, periodDays, inclinationDeg, phaseDeg)`; `SolarSystemWindow` updates each moon's position per-frame as `host.Position + R(angle)` (same circular-orbit pattern as Earth's Moon, with per-moon phase offsets so the Galileans aren't stacked). Bodies modelled: Io, Europa, Ganymede, Callisto (host = Jupiter) + Titan (host = Saturn). Real-scale mode swaps in the published km orbit radii via `OrbitalMechanics.KmToWorldRealScale`. |
| S7 | Dwarf planets | ✅ Done. `Planet.CreateDwarfPlanets()` returns Ceres, Pluto, Haumea, Makemake, Eris with full J2000 Keplerian elements; `SolarSystemWindow` appends them after Neptune so they automatically inherit the orbit-line, trail, picking and info-panel pipelines. Numeric focus shortcuts (`1`–`8`) stay bound to the major planets — dwarfs are reachable via click / double-click. |
| S8 | Constellation overlay | ✅ Done. `Constellations` loads RA/Dec line endpoints from `data/constellations.json` and renders them skybox-style (translation stripped from the view matrix + `gl_Position.z = w` so the figures sit on the celestial sphere at infinity, independent of camera position or scale mode). Names are drawn via the existing `BitmapFont` overlay anchored to `camera.Eye + dir * R`. Toggle with `C`. Ships with Orion, Ursa Major, Cassiopeia, Cygnus, Lyra, Crux, Scorpius, Leo. |

## 🛠 Quality-of-life

| # | Feature | Notes |
|---|---|---|
| Q1 | Smooth focus transitions | ✅ Done — see Top picks #2. |
| Q2 | Click-to-pick the Moon | ✅ Done. `SolarSystemWindow` now keeps a flat `_extraBodies` array (Moon + Galileans + Titan + Halley) and `TryPick` projects them alongside the planets. Selection / focus indices ≥ `_planets.Length` address into this array via the new `GetBody(int)` helper, so single-click info, double-click focus, smooth transition tracking and `ToggleRealScale` zoom-fit all transparently work for non-planet bodies. |
| Q3 | Search bodies by name | ✅ Done. `Ctrl+F` opens a top-of-screen modal prompt mirroring the date-seek lifecycle; typing live-filters every focusable body (Sun + planets + dwarfs + Moon + major moons + comet) by case-insensitive prefix-then-substring match, with the top 5 candidates previewed. `Enter` focuses the best match (and updates the info panel); `Esc` cancels. |
| Q4 | Screenshot key | ✅ Done. `F12` calls `SaveScreenshot` which `GL.ReadPixels` BGRA from the back buffer (post-bloom composite), copies rows into a `System.Drawing` `Bitmap` flipped vertically, and saves PNG to `screenshots/screenshot_yyyyMMdd_HHmmss.png`. Windows-only via `[SupportedOSPlatform("windows")]`; the keypath is gated by `OperatingSystem.IsWindows()` so non-Windows builds compile cleanly. A short status banner ("Saved …") reuses the date-seek feedback overlay. |
| Q5 | Persisted settings | ✅ Done. `TryLoadPersistedState` / `TrySavePersistedState` round-trip a `PersistedState` POCO via `System.Text.Json` to `%AppData%/SolarSystem/state.json`. Saved fields: camera (yaw/pitch/distance/target), `_simDays`, `_daysPerSecond`, `_paused`, `_focusIndex`, every UI toggle, solar-wind / flares enabled flags and `OrbitalMechanics.RealScale`. Load runs at the end of `OnLoad` (after the world is fully built); save runs first thing in `OnUnload`. RealScale is applied first via `ToggleRealScale` so the camera distance is clamped against the right limits. |
| Q6 | Mouse hover tooltip | ✅ Done. `OnMouseMove` caches `_mousePos`; once per frame `OnUpdateFrame` runs `TryPick(_mousePos)` and stores `_hoverIndex`. The render pass draws a tiny "Name\n0.000 AU" tooltip (heliocentric distance) anchored next to the cursor whenever the hover hits a body. Suppressed while the date-seek or name-search prompt is open. |
| Q7 | FPS / particle-count overlay | ✅ Done. `~` (`Keys.GraveAccent`) toggles `_showHud`. The HUD smooths FPS over a 0.5 s window (`_fpsAccum` / `_fpsFrames`) and prints FPS, scale mode, live wind / flare / comet-tail particle counts (`ActiveCount / MaxParticles`) and the asteroid-belt count, top-right corner. |

## 🌌 Real-scale mode UX

| # | Feature | Notes |
|---|---|---|
| R1 | Logarithmic depth buffer | ✅ Done — VS-side `gl_Position.z` remap (Outerra-style) in every 3D shader; no `gl_FragDepth` writes so early-Z is preserved. |
| R2 | Screen-space minimum body size | ✅ Done — `PlanetVS` expands sphere vertices outward from `uPlanetCenter` when projected radius < `uMinPixelRadius` px. |
| R3 | Adaptive star brightness | Make stars fainter when far from Sun (deep space), more saturated near a planet. |
| R4 | "Light-time" visualisation | Optional toggle that delays the Sun's lighting by the actual `r/c` light travel time at each planet. Tiny but cute. |

## 🚀 Architecture / refactor

| # | Item | Notes |
|---|---|---|
| A1 | Cross-platform font fallback | ✅ Done. `BitmapFont` was rewritten on top of SkiaSharp 2.88.8 (with `SkiaSharp.NativeAssets.Linux.NoDependencies` for Linux runtimes). The Windows-only `[SupportedOSPlatform("windows")]` GDI+ path is gone: a typeface fallback chain (`Segoe UI` → `DejaVu Sans` → `Arial` → `SKTypeface.Default`) feeds an `SKFont` with `SubpixelAntialias` edging, glyphs are drawn into a scratch `SKBitmap`, the alpha-coverage ink box is found, and the tight sub-rect is blitted into an RGBA8888 atlas uploaded to GL — same `Glyph` (UV/size/offset/advance) public surface as before, so call sites are unchanged. |
| A2 | Move shader source to `.glsl` files | ✅ Done. All ~30 shaders now live under `Resources/Shaders/*.glsl` and ship via `<None CopyToOutputDirectory="PreserveNewest">`. New `ShaderSources` static helper resolves files relative to `AppContext.BaseDirectory` (with a CWD fallback), caches them in a `ConcurrentDictionary`, and exposes `Load(name)` / `CreateProgram(vs, fs)`. `Renderer.CompileShaders`, `Constellations.Initialize`, and every particle system now build their `ShaderProgram`s through `ShaderSources`; the embedded C# string constants were deleted. |
| A3 | Move planet data to `planets.json` | ✅ Done. `Planet.CreateAll()` / `Planet.CreateDwarfPlanets()` first try `data/planets.json` (System.Text.Json, comments + trailing commas tolerated) and fall back to the original built-in tables if the file is missing or fails to parse. Same J2000 elements as before, but new bodies (or tweaks to existing ones) can be added without recompiling. |
| A4 | Replace point-sprite particles with instanced quads | ✅ Done. New shared `InstancedQuadParticles` helper owns a static 4-vertex quad VBO (triangle-strip BL/BR/TL/TR) plus a per-system dynamic instance VBO of `vec4(pos.xyz, life01)`, wired up with `glVertexAttribDivisor` and drawn via `glDrawArraysInstanced(TriangleStrip, 0, 4, count)`. `SolarWind`, `SolarFlares`, `Comet` (tail) and `AsteroidBelt` were rewritten on top of it and now share a single `particle.vert` whose `uPxBase / uPxMin / uPxMax / uLifeLo / uLifeHi / uViewportSize` uniforms reproduce the legacy `gl_PointSize` curve in clip space, so quad sizes are now driver-independent. `SolarSystemWindow` pushes the live viewport via `SetViewport` from `OnLoad` and `OnResize`. |
| A5 | Frame-time-independent particle systems | ✅ Done. The dynamic systems (`SolarWind`, `SolarFlares`, `Comet`) split each frame's `dt` into `ceil(dt / MaxSubStep)` fixed sub-steps (`MaxSubStep = 1/60 s`, capped at 16 iterations) before running emission + integration, so high `_daysPerSecond` no longer aliases particle motion or burst timers. `AsteroidBelt` remains analytic and is unaffected. |

---

If you want to claim any of these, open a PR or just start implementing — most items are self-contained and shouldn't conflict with each other.

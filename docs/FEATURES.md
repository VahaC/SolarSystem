# 🌌 Solar System — Feature Reference

This document is a detailed, feature-by-feature reference of everything the Solar System simulation currently ships with. For each feature you'll find:

- **What it is** — a short description.
- **What it does / shows** — the user-visible behaviour.
- **Why it exists** — what the feature is for.
- **How to enable / disable it** — keyboard shortcut, settings panel row, CLI flag, etc.
- **Notes** — implementation details, persistence, files involved, caveats.

> Most toggles persist to `%AppData%/SolarSystem/state.json` between sessions, so the UI you leave is the UI you come back to.

---

## Table of contents

1. [Top-tier visual features](#top-tier-visual-features)
2. [Visual polish (V1–V15)](#visual-polish-v1v15)
3. [Simulation features (S1–S16)](#simulation-features-s1s16)
4. [Quality-of-life (Q1–Q15)](#quality-of-life-q1q15)
5. [Real-scale mode UX (R1–R8)](#real-scale-mode-ux-r1r8)
6. [Architecture / tooling (A1–A12)](#architecture--tooling-a1a12)
7. [Global keyboard cheat sheet](#global-keyboard-cheat-sheet)

---

## Top-tier visual features

### Bloom / HDR glow
- **What it is:** a fullscreen post-process that makes bright pixels (the Sun, flares, aurora, particle tails) bleed into surrounding pixels.
- **What it does:** extracts highlights → 6-pass separable Gaussian blur → adds the result back on top of the scene.
- **Why:** instantly raises the perceived production value; makes the Sun *feel* hot.
- **Implementation:** RGBA16F offscreen target in `Renderer` (`BeginScene` / `EndSceneAndApplyBloom`).
- **Toggle:** Settings panel (`F1`) → **Bloom**.

### Smooth focus transitions + planet trails
- **What it is:** when you double-click or use number keys to focus a body, the camera smoothly slides toward it instead of snapping; each planet leaves a fading "comet trail" of its recent positions.
- **What it shows:** a 0.5-second smoothstep lerp on `Camera.Target` and `Camera.Distance` (tracking the body's live position en route), and a 200-sample ring buffer per planet rasterised as a per-vertex-alpha-fading `LineStrip`.
- **Toggle (trails):** `T`. Trails auto-clear on direction reverse and on scale-mode toggle.

### Logarithmic depth + minimum-pixel dots
- **What it is:** a fix for real-scale mode (R1 / R2). Without this, real-scale's astronomical near/far ratio causes z-fighting and tiny planets vanish below 1 px.
- **What it does:**
  - Log-depth: every 3D shader writes `gl_Position.z = (log2(1 + w) * Fcoef - 1) * w` with `Fcoef = 2 / log2(far + 1)`.
  - Minimum size: `PlanetVS` radially expands sphere vertices outward from `uPlanetCenter` whenever the projected radius drops below `Renderer.MinPixelRadius` (≈1 px → ≈2 px diameter dot).
- **Toggle:** automatic — both kick in whenever real-scale mode is on.

---

## Visual polish (V1–V15)

### V1 — Bloom / HDR glow
See [Top-tier visual features → Bloom](#bloom--hdr-glow).

### V2 — Atmospheric rim-light
- **What:** Fresnel `pow(1-dot(N,V), 3)` term tinted by atmosphere colour, applied to Earth, Venus, Jupiter, Neptune.
- **Why:** the limb of an atmospheric body should glow softly toward the camera.
- **Toggle:** built-in on supported bodies; controlled together with V9.

### V3 — Earth cloud layer
- **What:** a second sphere drawn at `1.012 × VisualRadius` over Earth, alpha derived from cloud-texture luminance (`smoothstep(0.18, 0.92, lum)`), drifting westward at `(spinRate − 0.08 rev/sim-day)` so the layer doesn't spin in lockstep with the surface.
- **Asset:** `textures/8k_earth_clouds.jpg`. Missing file → no clouds, no error.
- **Toggle:** automatic when the texture is present.

### V4 — Earth night-side city lights
- **What:** emissive city lights only on the dark side of the terminator. `PlanetFS` adds `night × (1 − smoothstep(-0.05, 0.2, NdotL))` to the lit colour.
- **Asset:** `textures/8k_earth_nightmap.jpg`.
- **Toggle:** automatic when the texture is present.

### V5 — Saturn ring shadows (both directions)
- **What:** the rings cast a shadow on Saturn, and Saturn casts a shadow on the rings.
  - On Saturn's lit hemisphere, `PlanetFS` casts a ray toward the Sun, intersects the ring plane, and if the hit point is within inner/outer radii samples the ring texture's alpha and attenuates diffuse + specular by `1 − α`.
  - In `RingFS`, each ring fragment ray-marches toward the Sun against Saturn's sphere; on hit, the fragment is darkened to 25%.
- **Toggle:** automatic for Saturn.

### V6 — Lens flare
- **What:** 6 coloured ghosts along the sun-through-screen-centre axis when the Sun is roughly looked-at. Drawn after bloom so the Gaussian blur doesn't smear the ghosts.
- **Toggle:** automatic; faded when the Sun is off-screen, hidden when behind the camera.

### V7 — Improved Milky Way sky
- **What:** `SkyFS` reconstructs a per-pixel world-space view direction and samples `_starsTexture` as an equirectangular map on a fullscreen far-plane quad.
- **Asset:** `textures/8k_stars_milky_way.jpg` (fallback: `8k_stars_milkyway.jpg`). Missing → tiny solid-colour procedural fallback.
- **Toggle:** automatic.

### V8 — Eclipses & body shadows
- **What:** any body can cast a soft-edged shadow on any other body. `PlanetFS` accepts up to 16 shadow-caster spheres; each surface fragment ray-tests them and applies `smoothstep(r·0.85, r, d)` to attenuate `diff + spec`.
- **Casters uploaded each frame:** Moon + Galileans + Titan + planets, capped at 16 (`SolarSystemWindow.BuildShadowCasters`).
- **Toggle:** automatic.

### V9 — Atmospheric scattering
- **What:** Rayleigh `0.75·(1+μ²)` + Henyey-Greenstein Mie (`g = 0.76`) phase functions on top of V2's Fresnel rim, with a soft terminator wrap.
- **Bodies:** Earth (cool blue), Mars (rust), Venus (sulphur), Titan (orange haze), Neptune (deep blue) — see `Renderer.GetAtmosphere`.
- **Eclipse-aware:** multiplied by V8's eclipse term, so a planet in totality goes properly dark.

### V10 — ACES tone mapping + auto-exposure
- **What:** `composite.frag` applies the Krzysztof Narkowicz ACES filmic curve to `(scene + bloom·strength) · uExposure`. With `Renderer.AutoExposureEnabled = true`, the HDR target's 1×1 mip drives an automatic exposure correction toward middle-grey 0.18 (clamped 0.25–4.0).
- **Toggle:** Settings panel.

### V11 — FXAA
- **What:** `fxaa.frag.glsl` runs a compact FXAA 3.x (5-tap luma neighbourhood, 0.0312 / 12.5% contrast threshold, directional 4-tap blur with luma-range fallback).
- **Toggle:** Settings panel → **FXAA**. `Renderer.FxaaEnabled = true` by default.

### V12 — Sun corona / surface granulation
- **What:** `sun.frag.glsl` adds a 4-octave fbm of `normalize(vNormal)*4 + (0, t*0.04, 0)` driving a per-fragment pulse and a hot/cool tint, so granules ripple across the disc and never align with the texture seam.
- **Toggle:** `U` (`Renderer.CoronaEnabled`, persisted).

### V13 — Aurora at Earth & Jupiter poles
- **What:** 128-segment triangle-strip ribbons between latitude bands 72°–80°, with curtain waves (`sin(angle*6 + t*0.8) + sin(angle*13 - t*1.3)`) and shimmer; rendered additively so they feed bloom.
- **Colours:** Earth = cool green; Jupiter = magenta/violet. Intensity halves when solar wind is off.
- **Toggle:** `K` (persisted).

### V14 — PBR planet shading
- **What:** Cook-Torrance with GGX (`D`), Schlick-GGX geometry (`G`), Schlick Fresnel (`F`) over `F0 = mix(0.04, base, metallic)`. Energy-conserving diffuse via `kd = (1-F)(1-metallic)`. Per-body roughness/metallic in `Renderer.GetPbr`.
- **Fallback:** legacy Phong when `uPbrEnabled = 0`.
- **Toggle:** `I` (`Renderer.PbrEnabled`, persisted).

### V15 — Specular ocean mask (Earth)
- **What:** specular term is multiplied by an ocean-mask red channel so only oceans glint and continents stay matte.
- **Asset:** `textures/8k_earth_specular_map.{png,jpg,tif}`.
- **Toggle:** `Q` (`Renderer.OceanMaskEnabled`, persisted).

---

## Simulation features (S1–S16)

### S1 — Pause & reverse time
- **What:** time controls beyond just speeding up.
- **Keys:**
  - `Space` — pause / resume.
  - `,` — play backward.
  - `.` — play forward.
  - `+` / `-` — change speed magnitude (works in both directions).

### S2 — Planet trails
- **What:** 200-sample ring buffer per planet rendered as a fading `LineStrip` (alpha quadratic in age).
- **Toggle:** `T`. Auto-clears on direction reverse and on scale toggle.

### S3 — Asteroid belt
- **What:** 8 000 asteroids with precomputed Keplerian elements + perifocal→world basis (ecliptic→GL swap folded in). Each frame each rock advances via Newton-Raphson Kepler solve and renders as additively-blended instanced quad with logarithmic depth.
- **Toggle:** Settings panel (asteroids are part of the default scene; the count is shown in the `~` HUD).

### S4 — Comet with ion / dust tail
- **What:** Halley-like ellipse (a≈17.83 AU, e≈0.967, i=162°) through `OrbitalMechanics.HeliocentricPosition`, drawing its own orbit polyline + a CPU particle tail in a cone around the anti-Sun axis. Tail intensity scales by `1/r` so it only blazes near perihelion.
- **Toggle:** automatic; visible whenever comets are enabled.

### S5 — Date seek
- **What:** jump simulation time to a specific date or by a delta.
- **Keys:** `J` opens the prompt at the top of the screen.
  - Type `YYYY-MM-DD` (any culture-invariant `DateTime.TryParse` format) and press `Enter`.
  - Or a signed delta: `+30`, `-365`.
  - `Esc` cancels.
- **Note:** trails are cleared after a jump so they don't draw a stale arc across the new epoch.

### S6 — Major moons
- **What:** Io, Europa, Ganymede, Callisto (host = Jupiter) and Titan (host = Saturn). Each is a `Planet` body wrapped by `Moon (hostIndex, orbitRadiusKm/artistic, periodDays, inclinationDeg, phaseDeg)`. Real-scale mode swaps in published km radii.
- **Click-to-pick:** yes (Q2).

### S7 — Dwarf planets
- **What:** Ceres, Pluto, Haumea, Makemake, Eris, with full J2000 Keplerian elements; appended after Neptune so they inherit orbit-line, trail, picking and info-panel pipelines.
- **Focus:** click / double-click (number keys 1–8 stay on the major planets).

### S8 — Constellation overlay
- **What:** RA/Dec line endpoints from `data/constellations.json`, drawn skybox-style on the celestial sphere at infinity (translation stripped from view matrix + `gl_Position.z = w`). Names rendered via `BitmapFont` anchored to `camera.Eye + dir * R`.
- **Ships:** Orion, Ursa Major, Cassiopeia, Cygnus, Lyra, Crux, Scorpius, Leo.
- **Toggle:** `C`.

### S9 — Spacecraft & probes
- **What:** Voyager 1, Voyager 2, JWST and ISS as additive 3-axis crosses with bitmap-font labels.
  - Voyagers: fixed escape direction + linear AU/yr drift from launch.
  - JWST: parked anti-Sun of Earth at the Sun–Earth L2 distance.
  - ISS: 92.68-min circular Earth orbit at 6 778 km (real-scale) or `Earth.VisualRadius * 1.3` (compressed).
- **Toggle:** `P`.

### S10 — Lagrange points
- **What:** L1..L5 every frame for Sun–Earth and Sun–Jupiter pairs, using analytic CR3BP approximations: `R · (μ/3)^(1/3)` for L1/L2, `R · (1 + 5μ/12)` for L3, equilateral apexes for L4/L5.
- **Render:** small additive diamond + label.
- **Toggle:** `G`.

### S11 — Meteor showers
- **What:** 7 peaks (Quadrantids, Lyrids, Eta Aquariids, Perseids, Orionids, Leonids, Geminids) as `(month, day, RA/Dec radiant, rate)`. Within ±3 days of a peak, short additive streaks emit from a disk perpendicular to the radiant just sunward of Earth.
- **Banner:** bottom-right names the active shower.
- **Toggle:** `M`.

### S12 — Eclipse / transit calendar
- **What:** hand-curated list of notable Sun–Earth–Moon alignments and Mercury / Venus transits 1999–2045 (`Bookmarks`).
- **Keys:**
  - `Ctrl+B` — next bookmark after current sim time (wraps).
  - `Ctrl+Shift+B` — previous bookmark (Q8).
- **Note:** trails are cleared and a banner with the event title is shown.

### S13 — Tidal locking visualisation
- **What:** a small additive 3-segment arrow on each spin-locked moon (Earth's Moon, the Galileans, Titan), pointing at the host planet.
- **Toggle:** `F4` (persisted via `PersistedState.ShowTidalLock`).

### S14 — Planetary alignment indicator
- **What:** computes each major planet's heliocentric ecliptic longitude per frame, runs union-find with a 12° threshold, and reports every component of size ≥ 3 as a `Group(indices, names)`. Renders an additive ray from the Sun through every member.
- **Banner:** top-right names the participants.
- **Toggle:** `F5`.

### S15 — N-body perturbation mode
- **What:** velocity-Verlet (kick-drift-kick leapfrog) on the major planets in (AU, days, M⊙) units with `GM_sun = 4π²/365.25² ≈ 2.959e-4 AU³/d²`. Sun is fixed at the origin; mutual gravity uses each planet's tabulated mass. Adaptive sub-stepping caps `|dt|` at 0.5 d/step (200 steps/frame max). Resyncs from analytic Kepler + 1-day finite-difference velocity on toggle/jump/scrub.
- **Banner:** "N-body mode" stays visible while active.
- **Toggle:** `F6` (persisted).
- **Note:** dwarfs / moons / comets stay analytic so the contrast is the whole point.

### S16 — Real comet catalogue
- **What:** JSON-driven catalogue (`data/comets.json`) of well-known comets — Halley, Hale–Bopp, NEOWISE, Encke — each with full Keplerian elements + per-comet tail tuning (`emissionRate`, `tailLifetime`, `tailSpeed`). Each gets its own orbit polyline, particle tail, label, picking entry, HUD line.
- **Fallback:** missing/parse-error file → built-in single Halley.

---

## Quality-of-life (Q1–Q15)

### Q1 — Smooth focus transitions
See [Top-tier → Smooth focus transitions](#smooth-focus-transitions--planet-trails).

### Q2 — Click-to-pick non-planet bodies
- **What:** Moon + Galileans + Titan + Halley are kept in `_extraBodies`. `TryPick` projects them alongside the planets so click-info, double-click-focus, smooth transitions and `ToggleRealScale` zoom-fit all work for them transparently.

### Q3 — Search bodies by name
- **Keys:** `Ctrl+F` opens a top-of-screen modal prompt; type to filter (case-insensitive prefix-then-substring) Sun + planets + dwarfs + Moon + major moons + comet. Top 5 candidates previewed.
  - `Enter` — focus the best match.
  - `Esc` — cancel.

### Q4 — Screenshot key (Windows path)
- **Key:** `F12`. Saves to `screenshots/screenshot_yyyyMMdd_HHmmss.png`.
- **Note:** superseded on non-Windows by Q11's cross-platform implementation.

### Q5 — Persisted settings
- **What:** `%AppData%/SolarSystem/state.json` round-trips a `PersistedState` POCO via `System.Text.Json`.
- **Saved:** camera (yaw/pitch/distance/target), `_simDays`, `_daysPerSecond`, `_paused`, `_focusIndex`, every UI toggle, solar-wind / flares enabled flags, `OrbitalMechanics.RealScale`.
- **Lifecycle:** load at end of `OnLoad` (after world is built), save first thing in `OnUnload`. RealScale is applied first via `ToggleRealScale` so distance is clamped against the right limits.

### Q6 — Mouse hover tooltip
- **What:** once per frame `OnUpdateFrame` runs `TryPick(_mousePos)`; on hit, a tiny `Name\n0.000 AU` tooltip is drawn next to the cursor.
- **Suppressed:** while date-seek or name-search prompts are open.

### Q7 — FPS / particle-count overlay
- **Key:** `~` (`Keys.GraveAccent`). FPS smoothed over a 0.5 s window. Shows: FPS, scale mode, live wind / flare / comet-tail particle counts (`Active / Max`), asteroid-belt count.

### Q8 — Bookmarks
- **What:** loads `data/bookmarks.json` (System.Text.Json, comments + trailing commas tolerated), falls back to built-in catalogue when missing.
- **Keys:** `Ctrl+B` next, `Ctrl+Shift+B` previous.
- **Side-effect:** clears trails, plays Q15 "tick", shows banner `kind: title — yyyy-MM-dd`.

### Q9 — Timeline scrubber
- **Key:** `V` toggles. 60-cell `█/░` bar at the bottom maps mouse-X to ±100 yr around J2000. While dragging, the regular `_simDays += daysPerSecond·dt` advance is suppressed; trails clear when sim time jumps > 0.5 d.

### Q10 — Cinematic camera paths
- **What:** 9 waypoint slots, each capturing `Yaw / Pitch / Distance / Target`. Playback uses a 4-point Catmull-Rom spline over 6 s (positions, yaw, pitch, distance interpolated independently).
- **Keys:**
  - `Ctrl+1..9` — record / overwrite.
  - `Ctrl+Shift+1..9` — clear one slot.
  - `Shift+P` — play.
  - `Ctrl+Shift+P` — clear all.
- **Persistence:** `%AppData%/SolarSystem/campath.json`. While playback runs, the per-frame "follow focused body" branch is skipped.

### Q11 — Cross-platform screenshot
- **What:** `SaveScreenshot` uses `glReadPixels` RGBA8, flips rows, hands pixels to a SkiaSharp `SKBitmap` and writes a 95-quality PNG.
- **Key:** `F12` (no longer Windows-only).

### Q12 — In-app settings panel
- **Key:** `F1` toggles. Hand-rolled "ImGui-lite" overlay drawn through `Renderer.DrawText` — no new GL state. 17 toggle rows + speed slider, all backed by closures over the same fields the keyboard shortcuts touch so the two stay in sync.
- **Mouse:** clicks hit-test against per-row bounding boxes and consume LMB so the camera doesn't yank.
- **Adaptive height:** the panel is clamped to the viewport, and when the rows don't fit the user can mouse-wheel over the panel to scroll. Off-screen rows have their hit-boxes zeroed out so a click at the same screen Y can't toggle a hidden row.

### Q13 — Localisation
- **What:** `Localization.T(key)` over a flat `Dictionary<string,string>`. Built-in English defaults in code; runtime languages from `data/lang.<code>.json` (e.g. `lang.uk.json`). System culture auto-applied at startup if a matching file ships.
- **Key:** `F2` cycles through every discovered language. Persisted in `state.json`.

### Q14 — Help-overlay collapse
- **Key:** `Tab` cycles `_helpMode`:
  - `0` — full Controls panel + bottom-left info.
  - `1` — just date + speed at the top.
  - `2` — everything hidden.
- **Adaptive layout:** the Controls cheat sheet now flows into as many columns as it needs to fit the viewport, then shrinks the font (down to 8 px) if it's still too tall. On a 1280×720 monitor the full ~50-line list still fits without overflowing.
- **Discovery hint:** in mode `1` a dim line under the speed reads `Tab — help · F1 — settings · F3 — bookmarks · Ctrl+F — search`, so users can still find the menus while the cheat sheet is collapsed.
- **Persistence:** in `state.json`.

### Q15 — Mute / SFX
- **What:** `AudioService` plays a "whoosh" on `BeginFocusTransition` and a "tick" on date jump / bookmark / waypoint record / screenshot.
- **Implementation:** `Console.Beep` on Windows (dispatched on a `Task` so the main thread never blocks); graceful no-op on Linux/macOS.
- **Key:** `S` (persisted).

---

## Real-scale mode UX (R1–R8)

> Toggle real-scale mode itself by pressing `R`. This switches `OrbitalMechanics.RealScale`, swaps in real km radii / orbital radii for moons, and applies the camera-distance clamps for that mode.

### R1 — Logarithmic depth buffer
See [Top-tier → Logarithmic depth + minimum-pixel dots](#logarithmic-depth--minimum-pixel-dots).

### R2 — Screen-space minimum body size
Same. `PlanetVS` expands sphere vertices outward when projected radius < `uMinPixelRadius`.

### R3 — Adaptive star brightness
- **What:** `Renderer.StarsBrightness` and `Renderer.StarsSaturation` drive `SkyFS`. `SolarSystemWindow.UpdateAdaptiveStars` (once per frame before `DrawStars`) maps:
  - Camera distance from Sun → brightness lerp `0.85` (near) → `0.30` (deep space).
  - Distance to nearest body's surface → saturation lerp `1.6` (hugging a planet) → `0.85` (empty space).
- **Toggle:** automatic; reference radii flip between real / compressed scale so the feel carries over.

### R4 — Light-time visualisation
- **What:** each planet's spin angle (and its cloud layer) is evaluated at `simDays - r/c` instead of `simDays`, where `r` is heliocentric distance and `c = 173.1446 AU/day`. Body positions stay current; only the lit longitude shifts.
- **Visible effect:** ~2° at Earth, ~90° at Neptune.
- **Key:** `Y` (persisted).

### R5 — Distance ruler *(planned)*
- **What:** hold `Shift` while clicking two bodies to draw a labelled line ("Earth → Mars: 0.524 AU, 4.36 light-min").
- **Status:** roadmap.

### R6 — Auto fit-to-orbit *(planned)*
- **Key:** `Z` — frame the focused body's full orbit on screen.
- **Status:** roadmap.

### R7 — 2D orrery mode *(planned)*
- **What:** top-down orthographic projection with all orbits flattened to the ecliptic.
- **Status:** roadmap.

### R8 — Light-time echo marker *(planned)*
- **What:** when R4 is on, draw a faint "echo" sphere where the planet WAS when the photons left + great-circle arc echo → current.
- **Status:** roadmap.

---

## Architecture / tooling (A1–A12)

### A1 — Cross-platform font fallback
- **What:** `BitmapFont` runs on SkiaSharp 2.88.8 (`SkiaSharp.NativeAssets.Linux.NoDependencies` for Linux). Typeface fallback chain: `Segoe UI` → `DejaVu Sans` → `Arial` → `SKTypeface.Default`. `SubpixelAntialias` edging. Glyphs are drawn into a scratch `SKBitmap`, ink box found via alpha coverage, blitted into an RGBA8888 atlas. Same `Glyph` public surface as before.

### A2 — Shaders in `.glsl` files
- **Where:** `Resources/Shaders/*.glsl`, shipped via `<None CopyToOutputDirectory="PreserveNewest">`.
- **Helper:** `ShaderSources.Load(name)` / `CreateProgram(vs, fs)`, resolving relative to `AppContext.BaseDirectory` with a CWD fallback, results cached.

### A3 — Planet data in `planets.json`
- **What:** `Planet.CreateAll()` / `Planet.CreateDwarfPlanets()` first try `data/planets.json`, fall back to built-in tables on missing/parse error. Comments + trailing commas tolerated.

### A4 — Instanced-quad particles
- **What:** `InstancedQuadParticles` owns a 4-vertex quad VBO + per-system dynamic instance VBO of `vec4(pos.xyz, life01)`, wired via `glVertexAttribDivisor`, drawn with `glDrawArraysInstanced(TriangleStrip, 0, 4, count)`. Used by `SolarWind`, `SolarFlares`, `Comet` (tail), `AsteroidBelt`. `particle.vert` reproduces the legacy `gl_PointSize` curve in clip space so quad sizes are driver-independent.

### A5 — Frame-time-independent particles
- **What:** dynamic systems (`SolarWind`, `SolarFlares`, `Comet`) split each frame's `dt` into `ceil(dt / MaxSubStep)` fixed sub-steps (`MaxSubStep = 1/60 s`, capped at 16). High `_daysPerSecond` no longer aliases particle motion or burst timers. `AsteroidBelt` is analytic and unaffected.

### A6 — GLSL hot-reload
- **Key:** `F7` toggles a `FileSystemWatcher` over `Resources/Shaders/*.glsl`. Disk events are coalesced into a thread-safe queue; `OnUpdateFrame` calls `ShaderSources.PollPendingReloads` once per frame on the GL thread. `ShaderProgram.Reload` link-tests the new program first and only swaps `Handle` (and clears the uniform-location cache) on success — typos leave the previous program running, with the error in an on-screen banner.

### A7 — Headless render / video export
- **CLI:** `--render --from YYYY-MM-DD --to YYYY-MM-DD [--dt 1.0] [--frames N] [--fps 60] [--out render] [--ffmpeg path] [--video-out file.mp4] [--real-scale]`.
- **Behaviour:** `StartVisible = false`, persisted state untouched, sim time pinned to `From + FrameIndex * dt` per frame (deterministic), particle systems use a fixed `1/Fps` sub-step, each `SwapBuffers` is captured to `OutDir/frame_NNNNN.png` via `SaveScreenshotTo`. After the last frame, ffmpeg runs as `ffmpeg -y -framerate Fps -i frame_%05d.png -c:v libx264 -pix_fmt yuv420p -crf 18 out.mp4` and the window closes so the process exits.

### A8 — Compute-shader N-body *(planned)*
- Move the asteroid Kepler solve to a compute shader so 8 000 → 100 000+. Output goes straight into the instance VBO.

### A9 — `OrbitalMechanics` unit tests *(planned)*
- xUnit + GitHub Actions. Validate `SolveKepler`, `HeliocentricPosition`, `OrbitWorldScale` against canonical J2000 ephemerides.

### A10 — CI smoke build *(planned)*
- GitHub Actions matrix: `windows-latest`, `ubuntu-latest`, `macos-latest` running `dotnet build -c Release`.

### A11 — Native AOT *(planned)*
- Trim + AOT-publish on .NET 10. Requires removing reflection-y bits and switching `System.Text.Json` to source generators.

### A12 — Per-frame profiler overlay *(planned)*
- Extend the `~` HUD with cumulative GPU + CPU timing per pass (sky, planets, particles, bloom) using `GL_TIMESTAMP` queries.

---

## Global keyboard cheat sheet

| Key | Action |
|---|---|
| `Space` | Pause / resume |
| `,` / `.` | Reverse / forward time |
| `+` / `-` | Speed magnitude |
| `1`–`8` | Focus a major planet |
| `R` | Toggle real-scale mode |
| `T` | Planet trails |
| `J` | Date-seek prompt |
| `Ctrl+F` | Search bodies by name |
| `C` | Constellations |
| `P` | Probes / spacecraft |
| `G` | Lagrange points |
| `M` | Meteor showers |
| `Ctrl+B` / `Ctrl+Shift+B` | Next / previous bookmark |
| `F4` | Tidal-lock arrows |
| `F5` | Planetary-alignment indicator |
| `F6` | N-body mode |
| `F7` | GLSL hot-reload |
| `F1` | Settings panel |
| `F2` | Cycle language |
| `F12` | Screenshot |
| `Tab` | Cycle help-overlay mode |
| `~` | FPS / HUD |
| `V` | Timeline scrubber |
| `Y` | Light-time visualisation |
| `U` | Sun corona |
| `K` | Aurora |
| `I` | PBR shading |
| `Q` | Ocean specular mask |
| `S` | Mute / SFX |
| `Ctrl+1..9` | Record camera waypoint |
| `Ctrl+Shift+1..9` | Clear waypoint |
| `Shift+P` | Play camera path |
| `Ctrl+Shift+P` | Clear all waypoints |

> Note: `Ctrl+B` is intentionally **not** used as a camera-related shortcut — it is reserved for the bookmark "next" action and conflicts with system hotkeys are respected per project guidelines.

---

## Persistence summary

| File | Contents |
|---|---|
| `%AppData%/SolarSystem/state.json` | Camera, sim time, speed, paused, focus index, every UI toggle, scale mode, language. |
| `%AppData%/SolarSystem/campath.json` | The 9 camera-path waypoint slots. |
| `data/planets.json` | Major + dwarf planet Keplerian elements (optional override). |
| `data/comets.json` | Comet catalogue (S16). |
| `data/bookmarks.json` | Eclipse / transit / event bookmarks (Q8). |
| `data/constellations.json` | Constellation line endpoints (S8). |
| `data/lang.<code>.json` | Localisation tables (Q13). |
| `screenshots/` | PNG screenshots from `F12`. |

---

*Last updated against the state of `ROADMAP.md` at the time this document was generated.*

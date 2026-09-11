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
- **Toggle (trails):** `T` or F1 → Bodies → *Trails*. Trails auto-clear on direction reverse and on scale-mode toggle.

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
- **Toggle:** F1 → Effects → *Sun corona* (`corona`; `Renderer.CoronaEnabled`, persisted).

### V13 — Aurora at Earth & Jupiter poles
- **What:** 128-segment triangle-strip ribbons between latitude bands 72°–80°, with curtain waves (`sin(angle*6 + t*0.8) + sin(angle*13 - t*1.3)`) and shimmer; rendered additively so they feed bloom.
- **Colours:** Earth = cool green; Jupiter = magenta/violet. Intensity halves when solar wind is off.
- **Toggle:** F1 → Effects → *Aurora* (`aurora`, persisted).

### V14 — PBR planet shading
- **What:** Cook-Torrance with GGX (`D`), Schlick-GGX geometry (`G`), Schlick Fresnel (`F`) over `F0 = mix(0.04, base, metallic)`. Energy-conserving diffuse via `kd = (1-F)(1-metallic)`. Per-body roughness/metallic in `Renderer.GetPbr`.
- **Fallback:** legacy Phong when `uPbrEnabled = 0`.
- **Toggle:** F1 → Effects → *PBR* (`pbr`; `Renderer.PbrEnabled`, persisted).

### V15 — Specular ocean mask (Earth)
- **What:** specular term is multiplied by an ocean-mask red channel so only oceans glint and continents stay matte.
- **Asset:** `textures/8k_earth_specular_map.{png,jpg,tif}`.
- **Toggle:** F1 → Effects → *Ocean specular* (`oceanmask`; `Renderer.OceanMaskEnabled`, persisted; greyed out when the texture is missing).

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
- **Toggle:** `T` (or F1 → Bodies → *Trails*). Auto-clears on direction reverse and on scale toggle.

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
- **Toggle:** F1 → Bodies → *Constellations* (`constellations`).

### S9 — Spacecraft & probes
- **What:** Voyager 1, Voyager 2, JWST and ISS as additive 3-axis crosses with bitmap-font labels.
  - Voyagers: fixed escape direction + linear AU/yr drift from launch.
  - JWST: parked anti-Sun of Earth at the Sun–Earth L2 distance.
  - ISS: 92.68-min circular Earth orbit at 6 778 km (real-scale) or `Earth.VisualRadius * 1.3` (compressed).
- **Toggle:** F1 → Bodies → *Probes* (`probes`).

### S10 — Lagrange points
- **What:** L1..L5 every frame for Sun–Earth and Sun–Jupiter pairs, using analytic CR3BP approximations: `R · (μ/3)^(1/3)` for L1/L2, `R · (1 + 5μ/12)` for L3, equilateral apexes for L4/L5.
- **Render:** small additive diamond + label.
- **Toggle:** F1 → Bodies → *Lagrange* (`lagrange`).

### S11 — Meteor showers
- **What:** 7 peaks (Quadrantids, Lyrids, Eta Aquariids, Perseids, Orionids, Leonids, Geminids) as `(month, day, RA/Dec radiant, rate)`. Within ±3 days of a peak, short additive streaks emit from a disk perpendicular to the radiant just sunward of Earth.
- **Banner:** bottom-right names the active shower.
- **Toggle:** F1 → Simulation → *Meteors* (`meteors`).

### S12 — Eclipse / transit calendar
- **What:** hand-curated list of notable Sun–Earth–Moon alignments and Mercury / Venus transits 1999–2045 (`Bookmarks`).
- **Keys:**
  - `Ctrl+B` — next bookmark after current sim time (wraps).
  - `Ctrl+Shift+B` — previous bookmark (Q8).
- **Note:** trails are cleared and a banner with the event title is shown.

### S13 — Tidal locking visualisation
- **What:** a small additive 3-segment arrow on each spin-locked moon (Earth's Moon, the Galileans, Titan), pointing at the host planet.
- **Toggle:** F1 → Bodies → *Tidal-lock arrows* (`tidal`) (persisted via `PersistedState.ShowTidalLock`).

### S14 — Planetary alignment indicator
- **What:** computes each major planet's heliocentric ecliptic longitude per frame, runs union-find with a 12° threshold, and reports every component of size ≥ 3 as a `Group(indices, names)`. Renders an additive ray from the Sun through every member.
- **Banner:** top-right names the participants.
- **Toggle:** F1 → Bodies → *Alignment indicator* (`alignment`).

### S15 — Physics sandbox (Ephemeris / Physics / Compare)
- **What:** supersedes the majors-only "N-body perturbation mode" (tracked as S17 in `ROADMAP.md`). One `PhysicsWorld` integrates every massive body — the Sun (free, barycentric frame), 8 planets, 5 dwarfs, the Moon, the Galileans and Titan — while comets and the asteroid belt ride the same field as massless test particles (belt: `asteroidbelt.compute.glsl` mode 1 replays the planet positions the world recorded that frame; CPU fallback with the same formula, capped at 16 coarser steps per frame). Units AU / days / M⊙, `GM☉ = k² ≈ 2.959e-4 AU³/d²`.
- **Modes** (`simmode`, F1 → Simulation → *Simulation mode*, persisted in `Choices`):
  - *Ephemeris* — the analytic path, unchanged bit-for-bit (eclipse bookmarks still land to the minute). Constants greyed out ("switch to Physics or Compare").
  - *Physics* — the world is seeded from the ephemeris at the current date (position from the analytic model, velocity from a 5-point stencil; the Moon's seed is least-squares fitted over ±30 d so the truncated ELP series doesn't bias its mean motion; Galileans/Titan get the analytic circular velocity) and takes over every body.
  - *Compare* — physics drives the bodies; every ephemeris position is drawn as a translucent ghost (same mesh, alpha 0.3, no shadows / atmosphere) plus a dashed ghost → body link.
- **Constants** (`physics.g`, `physics.sunmass`, `physics.exponent`, `physics.lightspeed`, `mass.<body>` on the **Masses** tab; all logarithmic except the exponent): G multiplier, Sun mass, exponent `n` in `a = GM/rⁿ` (1.5–3.0, step 0.01), speed of light for the light-time delay, per-body mass 0.01×–100×. Changing a value never re-seeds. Buttons: *Reset constants* (`physics.resetconst`), *Reset masses* (`physics.resetmasses`), *Restart from ephemeris* (`physics.reinit`). Constants live in the `Physics` section of `state.json`.
- **Collisions** (`physics.collisions`, Simulation tab, on by default): every live pair is swept along the step just taken — heliocentric pairs over the global step, host + satellites and sibling satellites per planetocentric sub-step, so the chord of their curved motion stays short — and when the closest approach is below `R₁ + R₂` (real radii: Sun 695 700 km, planets / moons from the body table, comets 5 km) the two merge, perfectly inelastically. The heavier body survives (a massive body always beats a test particle) at the pair's centre of mass with the summed momentum, its mass and volume are summed (`R³ = R₁³ + R₂³`), the other is marked dead and parked on the survivor (its position keeps tracking it), its satellites are re-parented to the survivor — or to the survivor's host when the survivor is itself a moon — and a moon that swallows its own planet is promoted to heliocentric level. The hierarchy is rebuilt and every relative state re-derived from the absolute one, so nothing else moves; the energy / angular-momentum drift references are rebased. Comets hitting a massive body vanish (no mass change); belt rocks are not checked. UI: banner, a bloom flash on the survivor, the diagnostics card lists the count and the last three events (date, impact speed, impact energy `½μv²` in J) and says "absorbed by …" for a dead selected body; absorbed bodies drop out of rendering, orbits, labels, picking, search, shadows, ghosts, tidal arrows and the alignment indicator, a dead comet's tail fades out, and the body's *Masses* slider greys out ("absorbed in a collision"). Focus / selection on the absorbed body jump to the survivor. Off: bodies pass through each other (softened at 1e-5 AU). *Restart from ephemeris*, `Reset` and leaving the physics modes revive everything; merges are not persisted (the world is re-seeded on load).
- **Integrator:** kick-drift-kick leapfrog composed into a 4th-order Yoshida scheme (symplectic, reversible). Hierarchical step: global ≤ 0.5 d and adaptive to ≥ 500 steps per orbit of the fastest body (≈ 0.2 d with Mercury); each planet + satellites subsystem is sub-stepped in the planetocentric frame (Moon ≤ 0.05 d, Galileans ≤ 0.01 d, adaptive) with the direct-minus-indirect tidal terms of the Sun and the other planets, the planet's global state being the subsystem barycentre. Pair separations are floored at 1e-5 AU so nothing can NaN.
- **Measured:** planets' energy drift ≈ 2e-9 relative over 100 years; no spurious collision in a decade of the real system; Moon within 0.2° of ELP-2000 after 10 years; Mercury's spurious perihelion drift ≈ 5″/century (two-body), ≈ 560″/century with the planets (Newtonian expectation ≈ 531″ + wobble); with `n = 2.05` it precesses degrees per decade.
- **Time jumps:** in Physics / Compare a date seek, scrubber drag or bookmark integrates from the current time to the target (negative steps for jumps back) in per-frame chunks — 2000 global steps or 8 ms per frame — with a top-centre progress bar; about 20–30 s per century. Slower and only approximately reproducible compared with Ephemeris mode.
- **Diagnostics** (`physics.hud`, Simulation tab): mode banner top-right; card with the global step, steps this frame (global · satellite · belt), relative energy and angular-momentum drift since the seed, the constants, and the osculating `a`, `e`, `P` of the selected (or focused) body about its primary.
- **Accuracy vs reality:** the seed is the *mean*-element ephemeris, not the osculating state, so even with real constants the planets drift from the Standish orbits by ~0.1–0.7°/decade (Saturn worst — its short-period Jupiter terms are missing from the mean elements). Real-scale, light-time (scaled by the speed-of-light slider), trails, picking and focus work in every mode.
- **Headless:** `--render … --physics` seeds at `--from` and integrates exactly `--dt` days per frame with no CPU budget (deterministic).
- **Migration:** saves with `Features.nbody = true` (or the pre-registry `NBodyEnabled`) load as Physics mode; the *Realistic* preset selects Physics.

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
- **Toggle:** F1 → Interface → *Timeline* (`timeline`, persisted).

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

### Q12 — In-app settings panel (v2)
- **Key:** `F1` toggles (also the *Settings* toolbar button). Hand-rolled "ImGui-lite" overlay drawn through `Renderer.DrawText` + `Renderer.FillRect` — no new GL objects beyond a 1×1 white texture.
- **Generated from the registry:** `BuildSettingsPanel` walks `FeatureRegistry.Entries` and emits one `ToggleRow` per `Feature` (plus `ButtonRow`s for commands flagged `ShowInPanel`, e.g. Language / Screenshot / Quit), so a new feature shows up in the panel by being registered — nothing else to wire.
- **Tabs:** Bodies · Simulation · Effects · Post-FX · Interface · Developer (`FeatureCategory`). `←` / `→` cycle tabs while the panel is open; the active tab is persisted.
- **Per row:** `[x]` state, localised label, the bound chord right-aligned (reflects `keybindings.json` overrides), greyed-out with a reason when unavailable (GPU belt without compute support, ocean mask without its texture). Hovering shows the row's `ui.desc.<id>` text in the footer.
- **Presets** (scene tabs only): Cinematic / Realistic / Performance / Minimal = defaults + a small override set; the matching preset is highlighted. *Reset tab* / *All on* / *All off* act on the current tab only, so Interface / Developer switches (fullscreen, the panel itself) are never yanked.
- **Mouse:** clicks hit-test against per-row bounding boxes and consume LMB so the camera doesn't yank; wheel scrolls when the rows overflow the viewport (a slim scroll indicator appears). Off-screen and inactive-tab rows have their hit-boxes zeroed so a click can't flip a hidden row.
- **Esc** closes the panel (it no longer quits the app — see [Esc](#global-keyboard-cheat-sheet)).

### Command palette (Ctrl+K)
- **What:** a VS-Code-style modal prompt (`CommandPalette`) that live-filters every registry entry by localised label, id and description — prefix matches first, then word-start, substring, id, description.
- **Keys:** type to filter, `↑` / `↓` (or `Tab`) move, `Enter` toggles a feature (palette stays open so you can flip several) or runs a command (palette closes), `Esc` closes. Rows are also clickable.
- **Shows:** `[x]` state, category tag, bound chord, and the selected entry's description. Focus commands (`Focus: Mars`), waypoints, bookmarks, screenshot, language, quit — everything is searchable.

### Bottom toolbar
- **What:** `Toolbar` — a centred strip of text buttons: pause (`▮▮` / `▶`), speed `−` / value / `+` (click the value to reset to 1 d/s), direction (`▶▶` / `◀◀`), Orbits · Labels · Trails · Real scale, and *Settings* / *Commands* buttons. Active toggles are highlighted; hovering shows the feature description and chord.
- **Toggle:** Interface → Toolbar (`toolbar` id). Lifted above the timeline scrubber when that is visible.

### Feature registry & `keybindings.json`
- **What:** `FeatureRegistry` / `Feature` / `Command` (`Feature.cs`, `FeatureRegistry.cs`) — the single list every menu is derived from. A `Feature` carries `Get` / `Set` (with all side-effects: trail clears, integrator resync, focus fix-ups), `Default`, `Persist`, `LegacyKey`, optional `Unavailable` reason and `Banner`; a `Command` carries `Run` and an optional live `Status`.
- **Derived surfaces:** key dispatch (`OnKeyDown` → `FeatureRegistry.Dispatch`, exact Ctrl/Shift/Alt match), F1 panel, Ctrl+K palette, toolbar, the generated help overlay (`ui.help.mouse` / `ui.help.extra` static lines + every bound entry), and `state.json`.
- **Overrides:** `data/keybindings.json` is a flat `{"id": "Ctrl+Shift+P, Num1"}` object (comments and trailing commas allowed); an empty string unbinds. Unknown ids / unparsable chords are logged and skipped. The shipped file lists every id and has the legacy single-letter layout commented out.
- **Tests:** `SolarSystem.Tests/FeatureRegistryTests.cs`, `CommandPaletteTests.cs` and the extended `SettingsPanelTests.cs` cover chord parsing, dispatch, overrides, snapshot / restore / legacy migration, presets, palette search and the panel's tab / preset hit-testing.

### Q13 — Localisation
- **What:** `Localization.T(key)` over a flat `Dictionary<string,string>`. Built-in English defaults in code; runtime languages from `data/lang.<code>.json` (e.g. `lang.uk.json`). System culture auto-applied at startup if a matching file ships.
- **Key:** `F2` cycles through every discovered language. Persisted in `state.json`.

### Q14 — Help-overlay collapse
- **Key:** `Tab` cycles `_helpMode`:
  - `0` — full Controls panel + bottom-left info.
  - `1` — just date + speed at the top.
  - `2` — everything hidden.
- **Adaptive layout:** the cheat sheet flows into as many key/label columns as it needs to fit the viewport, then shrinks the font (down to 8 px) if it's still too tall.
- **Generated:** the full cheat sheet is built from the registry (static mouse lines from `ui.help.mouse` / `ui.help.extra`, then every bound entry with its live chord), so it can never drift from the real key map — including `keybindings.json` overrides.
- **Discovery hint:** in mode `1` a dim line under the speed reads `Tab — help · F1 — settings · Ctrl+K — commands · F3 — bookmarks`, so users can still find the menus while the cheat sheet is collapsed.
- **Persistence:** in `state.json`.

### Q15 — Mute / SFX
- **What:** `AudioService` plays a "whoosh" on `BeginFocusTransition` and a "tick" on date jump / bookmark / waypoint record / screenshot.
- **Implementation:** `Console.Beep` on Windows (dispatched on a `Task` so the main thread never blocks); graceful no-op on Linux/macOS.
- **Toggle:** F1 → Interface → *Audio* (`audio`, persisted).

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
- **Toggle:** F1 → Simulation → *Light-time delay* (`lighttime`, persisted).

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
- **Key:** `hotreload` (F1 → Developer) toggles a `FileSystemWatcher` over `Resources/Shaders/*.glsl`. Disk events are coalesced into a thread-safe queue; `OnUpdateFrame` calls `ShaderSources.PollPendingReloads` once per frame on the GL thread. `ShaderProgram.Reload` link-tests the new program first and only swaps `Handle` (and clears the uniform-location cache) on success — typos leave the previous program running, with the error in an on-screen banner.

### A7 — Headless render / video export
- **CLI:** `--render --from YYYY-MM-DD --to YYYY-MM-DD [--dt 1.0] [--frames N] [--fps 60] [--out render] [--ffmpeg path] [--video-out file.mp4] [--real-scale] [--physics]`.
- **Behaviour:** `StartVisible = false`, persisted state untouched, sim time pinned to `From + FrameIndex * dt` per frame (deterministic), particle systems use a fixed `1/Fps` sub-step, each `SwapBuffers` is captured to `OutDir/frame_NNNNN.png` via `SaveScreenshotTo`. After the last frame, ffmpeg runs as `ffmpeg -y -framerate Fps -i frame_%05d.png -c:v libx264 -pix_fmt yuv420p -crf 18 out.mp4` and the window closes so the process exits.

### A8 — Compute-shader N-body
- **Key:** `gpubelt` (F1 → Developer) toggles the GPU compute path on the asteroid belt.
- **What:** `Resources/Shaders/asteroidbelt.compute.glsl` runs one `gl_GlobalInvocationID.x` per asteroid (local size 64), reads per-body Keplerian elements from a static SSBO (binding 0, 3 × `vec4` per asteroid: `(a, e, n, M0)`, `(Ax.xyz, brightness)`, `(Bx.xyz, sqrt(1−e²))`), solves Kepler with the same 6-iteration Newton step the CPU path uses, and writes `vec4(pos.xyz, brightness)` straight into the instance VBO via SSBO binding 1. `glMemoryBarrier(VertexAttribArrayBarrier)` synchronises with the rasteriser. No CPU round-trip per frame, so the 8 000 cap can grow to 100 000+ without stalling the simulation thread.
- **Fallback:** if the compute shader fails to compile or link (legacy GL driver), `AsteroidBelt.GpuComputeAvailable` flips to `false` and the original CPU Kepler solver keeps the belt on screen. Persisted via `PersistedState.GpuAsteroidsEnabled`; available as a row in the F1 settings panel (`ui.settings.gpubelt`).

### A9 — `OrbitalMechanics` unit tests
- **What:** new `SolarSystem.Tests/OrbitalMechanicsTests.cs` (xUnit). Pinned invariants:
  - `SolveKepler(M, 0)` returns `M` mod 2π for any sample of `M ∈ [−π, π]`.
  - For every `e ∈ {0, 0.05, 0.2, 0.5, 0.7, 0.9, 0.95}` and `M ∈ [0, 2π)` step 0.31, the residual `(E − e·sin E) − M` (wrapped to `[−π, π]`) is < 1e-9.
  - `SolveKepler` normalises negative input to `[0, 2π)`.
  - `HeliocentricPosition(Earth, 0)` returns a position with `r ∈ [a(1−e), a(1+e)] ≈ [0.983, 1.017] AU`, `r ∈ [0.95, 1.05]`, `|y| < 0.01 AU` (Earth has zero ecliptic inclination).
  - `HeliocentricPosition(Mars, 0)` and `HeliocentricPosition(Mars, 1·period)` differ by < 0.02 AU (one-period closure under the secular-rate path).
  - Earth's `|y|` stays below 0.001 AU at every quarter-month sample.
  - `OrbitWorldScale` is monotonically decreasing in compressed mode across `a ∈ {0.39, 0.72, 1.0, 1.52, 5.20, 9.54, 19.19, 30.07}`, equals `AuToWorldRealScale` everywhere in real-scale mode, and lands Neptune within ±5 units of 200 world-units (the calibration target).
- **Run:** `dotnet test SolarSystem.Tests/SolarSystem.Tests.csproj`.

### A10 — CI smoke build
- **What:** GitHub Actions workflow at `.github/workflows/ci.yml` that builds and tests the project on every push / pull request to `main`.
- **Matrix:** `windows-latest`, `ubuntu-latest`, `macos-latest` with `fail-fast: false` so each OS reports independently.
- **Steps per leg:** `actions/checkout@v4` — `actions/setup-dotnet@v4` (`dotnet-version: 10.0.x`, `dotnet-quality: preview`) — `dotnet --info` — `dotnet restore` for both `SolarSystem.csproj` and `SolarSystem.Tests/SolarSystem.Tests.csproj` — `dotnet build -c Release --no-restore` for main + tests — `dotnet test -c Release --no-build` to run the xUnit suite (A9: `OrbitalMechanicsTests`, `LocalizationTests`, `SettingsPanelTests`).
- **Triggers:** `push` and `pull_request` on `main` for normal gating; `workflow_dispatch` so a maintainer can fire the matrix manually from the **Actions** tab (the enable/disable knob for ad-hoc smoke builds); `paths-ignore` skips runs for doc-only changes (`**/*.md`, `docs/**`, `.github/copilot-instructions.md`).
- **Permissions:** `contents: read` only — the workflow does not push artifacts or comment on PRs.
- **Badge:** the README links `https://github.com/VahaC/SolarSystem/actions/workflows/ci.yml/badge.svg?branch=main` so the current main-branch status is visible at a glance.

### A11 — Native AOT
- **What:** the project compiles cleanly under the .NET 10 trim / AOT analyzers and can be published as a single self-contained native binary with `dotnet publish -c Release -p:PublishAot=true -r <rid>`.
- **CSProj flags:** `IsAotCompatible`, `IsTrimmable`, `EnableTrimAnalyzer` and `EnableSingleFileAnalyzer` are on for every build (Debug + Release) so any new reflection-y addition lights up the warning list before it ever reaches a publish step. Native AOT itself is opt-in via the publish flag — interactive `dotnet run` keeps the JIT for fast iteration.
- **System.Text.Json source generators:** every (de)serialisation path in the project — `Bookmarks` (`bookmarks.json`), `CameraPath` (`%AppData%/SolarSystem/campath.json`), `CometCatalog` (`comets.json`), `Constellations` (`constellations.json`), `Localization` (`lang.<code>.json`), `Planet` (`planets.json`) and `SolarSystemWindow.PersistedState` (`state.json`) — now routes through a shared `[JsonSerializable]`-decorated `SolarSystemJsonContext : JsonSerializerContext` (see `JsonContext.cs`). Per-call-site `JsonSerializerOptions` (case-insensitive matching, comment / trailing-comma tolerance, `WriteIndented`) are wrapped with `new SolarSystemJsonContext(opts)` so the source-generated `JsonTypeInfo<T>` is still used and no reflection-based metadata resolver is pulled in.
- **DTOs:** `Bookmarks.JsonEntry`, `CometCatalog.CometsFile/CometDto`, `Constellations.ConstellationsFile/ConstellationDto`, `Planet.PlanetsFile/PlanetDto` and `SolarSystemWindow.PersistedState` were promoted from `private sealed class` to `internal sealed class` so the context can name them.
- **Dropped reference:** `System.Drawing.Common` (unused since Q11's SkiaSharp screenshot path) was removed from the csproj — it isn't AOT-friendly and pulled in a needless P/Invoke surface.
- **No runtime toggle:** AOT is purely build-time, so there is nothing to flip from the F1 settings panel; the regular `dotnet run` developer loop continues to use the JIT.

### A12 — Per-frame profiler overlay
- **What:** an on-screen card with per-pass GPU + CPU timings sampled directly from the OpenGL pipeline so you can see exactly where each frame's milliseconds go without an external tool.
- **Key:** `F10` toggles the overlay (`_showProfiler`); also exposed as a row in the F1 settings panel (`ui.settings.profiler`). The choice is persisted in `state.json` (`PersistedState.ShowProfiler`) and surfaced as a banner via `ui.profiler.on/off`.
- **Implementation:** `FrameProfiler` allocates a triple-buffered ring of `GL_TIME_ELAPSED` queries (one per pass per slot) and a parallel `Stopwatch`. `OnRenderFrame` calls `BeginFrame` (which harvests the slot 2 frames ago — already complete, so no `glGetQueryObject` stall), then wraps the renderer in `BeginPass(name)` / `EndPass()` markers. Five passes are tracked: `sky` (DrawStars + constellations + orbits + trails), `planets` (Sun + planets + moons + comet bodies + clouds + Saturn ring), `particles` (asteroid belt + comet tails + solar wind / flares + probes / Lagrange / meteors), `bloom` (`EndSceneAndApplyBloom` + lens flare) and `ui` (labels + tooltip + HUD + scrubber + settings + sidebar). Per-pass GPU + CPU times are EMA-smoothed (α = 0.15).
- **Overlay:** `DrawProfilerOverlay` renders a bottom-right card with one line per pass formatted as `gpu | cpu (ms)` plus a `total` row and the whole-frame CPU number. Localised pass labels (`ui.profiler.pass.*`) so the table reads naturally in every language.
- **Fallback:** if the driver doesn't expose `GL_TIME_ELAPSED` the profiler sets `GpuQueriesAvailable = false` and the overlay omits the GPU column — CPU numbers keep working.

### Borderless fullscreen toggle (Alt+Enter)
- **Key:** `Alt + Enter` flips between the normal window and `WindowState.Fullscreen` on the current monitor. Also exposed as a row in the F1 settings panel (`ui.settings.fullscreen`) so it can be enabled / disabled by mouse without the keyboard shortcut. The choice is persisted in `state.json` (`PersistedState.Fullscreen`) and re-applied on startup, so the window comes up in the same mode it was closed in.
- **Localised banners:** `ui.fullscreen.on` / `ui.fullscreen.off` are surfaced as the standard transient feedback line whenever the state flips.

---

## Global keyboard cheat sheet

Only the essentials are bound by default. Everything else is one click away in the `F1` panel or one
search away in `Ctrl+K`, and any id can be bound in `data/keybindings.json`.

| Key | Action |
|---|---|
| `F1` | Settings panel (tabs, presets, every toggle) |
| `Ctrl+K` | Command palette |
| `Tab` | Cycle help overlay: full → minimal → hidden |
| `Space` | Pause / resume |
| `,` / `.` | Reverse / forward time |
| `+` / `-` | Speed magnitude |
| `0` / `1`–`8` | Focus the Sun / a major planet |
| `O` / `L` / `T` | Orbits / labels / trails |
| `R` | Toggle real-scale mode |
| `J` | Date-seek prompt |
| `Ctrl+F` | Search bodies by name |
| `Ctrl+E` / `Ctrl+Shift+E` | Next / previous bookmark |
| `F3` | Bookmarks sidebar |
| `Ctrl+1..9` / `Ctrl+Shift+1..9` | Record / clear camera waypoint |
| `Shift+P` / `Ctrl+Shift+P` | Play / clear camera path |
| `F2` | Cycle language |
| `F9` | Start / stop video recording |
| `F10` | Per-frame profiler overlay |
| `F12` | Screenshot |
| `~` | FPS / HUD |
| `Alt+Enter` | Toggle borderless fullscreen |
| `Esc` | Close the open panel / prompt; on an empty screen, twice within 2 s quits |

**Unbound by default** (ids for `keybindings.json`): `axes`, `dwarfs`, `probes`, `lagrange`, `constellations`,
`tidal`, `alignment`, `meteors`, `lighttime`, `simmode`, `physics.g`, `physics.sunmass`, `physics.exponent`,
`physics.lightspeed`, `physics.hud`, `physics.collisions`, `physics.resetconst`, `physics.reinit`, `physics.resetmasses`, `mass.<body>`,
`solarwind`, `solarflares`, `corona`, `aurora`,
`atmosphere`, `eclipses`, `pbr`, `oceanmask`, `bloom`, `autoexposure`, `fxaa`, `lensflare`, `toolbar`,
`timeline`, `audio`, `hotreload`, `gpubelt`, `quit`. A key bound to `simmode` cycles the mode; one bound to a
slider resets it to its default.

> Note: `Ctrl+B` is intentionally **not** used — it collides with system hotkeys on some platforms, so bookmarks use `Ctrl+E`.

---

## Persistence summary

| File | Contents |
|---|---|
| `%AppData%/SolarSystem/state.json` | Camera, sim time, speed, focus index, help mode, language, active settings tab, `Features: {id: bool}` for every persistable registry toggle (pause, scale mode, every visual switch…), `Choices: {id: index}` for selectors (`simmode`), and a `Physics` section (`G`, `SunMassScale`, `GravityExponent`, `SpeedOfLightScale`, `BodyMassScale`). Pre-registry saves (one PascalCase bool per toggle) are migrated on first load; `nbody = true` becomes Physics mode. |
| `%AppData%/SolarSystem/campath.json` | The 9 camera-path waypoint slots. |
| `data/planets.json` | Major + dwarf planet Keplerian elements (optional override). |
| `data/comets.json` | Comet catalogue (S16). |
| `data/bookmarks.json` | Eclipse / transit / event bookmarks (Q8). |
| `data/constellations.json` | Constellation line endpoints (S8). |
| `data/lang.<code>.json` | Localisation tables (Q13). |
| `data/keybindings.json` | Optional hotkey overrides: `{"id": "chord, chord"}`. |
| `screenshots/` | PNG screenshots from `F12`. |

---

*Last updated against the state of `ROADMAP.md` at the time this document was generated.*

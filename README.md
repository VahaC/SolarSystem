# 🌌 Solar System

A real-time, physically-flavoured 3D simulation of our Solar System, written in C# 14 on .NET 10 with **OpenTK 4** (OpenGL 4.5). Eight planets orbit the Sun on Keplerian paths, spin on tilted axes, cast Phong-shaded highlights, drag a glittering solar wind in their wake, and float against a 360° Milky Way sky.

![Solar System](docs/screenshot.jpg)

---

## ✨ Features

### Bodies & motion
- **Keplerian orbits** — eccentricity, inclination, ascending node, argument of periapsis, mean anomaly at J2000. Position is solved from Kepler's equation each frame.
- **Eight real planets** with 8K diffuse textures (procedural fallback if a file is missing).
- **Five IAU dwarf planets** — Ceres, Pluto, Haumea, Makemake, Eris, with full J2000 elements; they reuse the same orbit / trail / picking pipeline as the majors.
- **Major moons** — Earth's Moon plus the Galileans (Io, Europa, Ganymede, Callisto) and Titan, each on its own circular inclined orbit around its host planet.
- **Asteroid belt** — 8000 rocks with precomputed Keplerian elements, advanced per frame by a Newton-Raphson Kepler solve, rendered as additively-blended `GL_POINTS`.
- **Comet** — a 1P/Halley-like ellipse (a≈17.83 AU, e≈0.967, i=162°) with its own orbit polyline and a CPU-particle ion/dust tail that ignites near perihelion (intensity ∝ 1/r).
- **Axial rotation & tilt**, including retrograde spin for Venus/Uranus.
- **Data-driven** — planet & dwarf elements live in `data/planets.json` (with comments + trailing commas); the built-in tables are a fallback.

### Rendering
- **Phong-lit shading** from a single point light at the origin (the Sun).
- **HDR + Bloom post-process** — the scene is rendered to an `RGBA16F` FBO, then bright-pass + 6-pass separable Gaussian blur + additive composite produce a glow halo on the Sun, flares and particles.
- **Earth cloud layer** — second alpha-blended sphere at `1.012 ×` Earth radius, slowly counter-rotating (alpha derived from cloud-texture luminance, so a plain JPG works).
- **Earth night-side city lights** — emissive nightmap added to the dark hemisphere via `1 − smoothstep(-0.05, 0.2, NdotL)`; smoothly fades across the terminator.
- **Saturn's rings + mutual shadow** — alpha-blended texture ring, properly tilted with the planet. The `PlanetFS` casts a ray from each lit Saturn fragment toward the Sun and attenuates lighting by the ring's per-radius opacity; the `RingFS` does the inverse ray-vs-sphere test so Saturn's shadow falls on the rings.
- **Lens flare** — screen-space additive ghosts along the Sun→centre axis when the Sun is roughly looked-at; fades with NDC distance and view alignment.
- **Sun corona / granulation** — `sun.frag` runs a 4-octave fbm of the world-space normal (drifting in time) plus a low-frequency pulse, so the disc breathes with hot/cool granules instead of staying a static texture.
- **Polar auroras** — additive ribbon mesh at Earth (green) and Jupiter (magenta/violet) poles, with curtain waves + per-vertex shimmer animated against `GLFW.GetTime()`; intensity boosted while the solar wind is on, and the bright crests feed the bloom pass.
- **PBR planet shading** — Cook-Torrance / GGX with Schlick-GGX G, Schlick F and per-body roughness/metallic constants (gas giants smoother, rocky bodies rougher, the Moon nearly Lambertian); legacy Phong lobe remains as the off-fallback.
- **Specular ocean mask** — Earth's `8k_earth_specular_map.{png,jpg,tif}` (TU3) gates the specular term so only oceans glint while continents stay matte.
- **Solar wind & flares** — instanced-quad particle systems streaming radially from the Sun (yellow→orange) and erupting sprites that feed the bloom pass.
- **Equirectangular Milky Way sky** when `8k_stars_milky_way.jpg` is present (procedural starfield otherwise).
- **Adaptive star brightness/saturation** — sky shader is dimmed in deep space and saturation-boosted close to a body, so the panorama doesn't drown out distant planets.
- **Constellation overlay** — RA/Dec line endpoints from `data/constellations.json`, rendered skybox-style at infinity (Orion, Ursa Major, Cassiopeia, Cygnus, Lyra, Crux, Scorpius, Leo).
- **Planet trails** — per-body 200-sample ring buffer rasterised as a fading line strip (alpha quadratic in age); auto-clears on direction reverse / scale toggle / date jump.
- **Cross-platform bitmap HUD** — SkiaSharp glyph atlas (Segoe UI → DejaVu Sans → Arial → default fallback chain) packed into an RGBA atlas, used for body labels and on-screen panels.

### Real-scale mode (`R`)
- **Logarithmic depth** — every 3D shader writes `gl_Position.z = (log2(1 + w) · Fcoef − 1) · w` (Outerra-style), eliminating z-fighting across the ~10⁷ near/far ratio.
- **Screen-space minimum body size** — `PlanetVS` expands sphere vertices outward from the planet centre when the projected radius drops below ~1 px, so distant bodies never collapse to nothing.

### Interaction
- **Click-to-pick** any body (planet / dwarf / Moon / Galilean / Titan / comet) for an info panel; **double-click** to focus the camera on it.
- **Smooth focus transitions** — `Camera.Target` and `Distance` lerped over ~0.5 s with a smoothstep ease, tracking the moving body en route.
- **Hover tooltip** — name + heliocentric distance shown next to the cursor.
- **Name search** — `Ctrl+F` opens a modal prompt with live prefix/substring matching across every focusable body.
- **Date seek** — press `J` to jump to an absolute `YYYY-MM-DD` or a signed delta in days (`+30`, `-365`).
- **Time control** — variable simulation speed, pause (`Space`), forward / backward direction (`,` / `.`).
- **Light-time toggle** (`Y`) — delays each planet's spin angle by `r/c` so the day/night terminator falls where the photons currently illuminating it left the Sun (~2° at Earth, ~90° at Neptune).
- **Screenshot** (`F12`, Windows) saves a PNG of the post-bloom composite to `screenshots/`.
- **HUD overlay** (`~`) shows FPS, scale mode, and live particle / asteroid counts.
- **Persisted UI state** — camera, sim time, every toggle and the scale mode round-trip via `%AppData%/SolarSystem/state.json`.
- **Focus cycling** with number keys; orbiting / panning / zoom with the mouse.

> 📍 Looking for what's coming next? See **[ROADMAP.md](ROADMAP.md)** for planned features and improvement ideas.

---

## 🎮 Controls

| Input | Action |
|---|---|
| **LMB drag** | Orbit camera around target |
| **MMB drag** | Pan target |
| **Mouse wheel** | Zoom |
| **LMB click** | Select body (shows info panel) |
| **LMB double-click body** | Focus camera on body (smooth 0.5 s transition) |
| **LMB double-click empty space** | Stop following (free camera) |
| **0** / **Numpad 0** | Reset to Sun view |
| **1 – 8** | Focus Mercury … Neptune |
| **+ / =** | Speed up time (×1.5, capped at 1000 d/s) |
| **− / _** | Slow down time (÷1.5, floor 0.1 d/s) |
| **Space** | Pause / resume |
| **,** / **.** | Play backward / forward (magnitude preserved) |
| **J** | Open date-seek prompt (`YYYY-MM-DD` or `±days`); `Esc` cancels |
| **O** | Toggle orbit lines |
| **T** | Toggle planet trails |
| **L** | Toggle labels |
| **A** | Toggle planet axis lines |
| **W** | Toggle solar wind |
| **F** | Toggle solar flares |
| **C** | Toggle constellation overlay |
| **R** | Toggle real-scale mode (km-derived radii + log depth) |
| **D** | Toggle dwarf planets |
| **Y** | Toggle light-time delay (`simDays − r/c` for spin) |
| **Ctrl+F** | Search bodies by name |
| **F12** | Screenshot to `screenshots/` (Windows) |
| **~** | Toggle FPS / particle-count HUD |
| **U** | Toggle sun corona / granulation (V12) |
| **K** | Toggle polar aurora ribbons (V13) |
| **I** | Toggle PBR planet shading (V14) |
| **Q** | Toggle ocean specular mask (V15) |
| **Esc** | Quit |

---

## 🚀 Build & Run

### Requirements

- **.NET 10 SDK** (preview or later)
- **Windows / Linux / macOS** — font rasterisation runs on SkiaSharp (with `SkiaSharp.NativeAssets.Linux.NoDependencies` for Linux); only the optional `F12` screenshot key is Windows-only.
- A GPU supporting OpenGL 4.5

### Run

```powershell
git clone https://github.com/VahaC/SolarSystem.git
cd SolarSystem
dotnet run -c Release
```

### Textures

Place 8K Solar System textures in a `textures/` folder next to the executable. Files used (all optional — missing files fall back to procedural placeholders):

```
textures/
├── 8k_sun.jpg
├── 8k_mercury.jpg
├── 8k_venus_surface.jpg
├── 8k_earth_daymap.jpg
├── 8k_earth_clouds.jpg          # V3: cloud layer
├── 8k_earth_nightmap.jpg        # V4: city lights
├── 8k_mars.jpg
├── 8k_jupiter.jpg
├── 8k_saturn.jpg
├── 8k_saturn_ring_alpha.png
├── 2k_uranus.jpg
├── 2k_neptune.jpg
├── 8k_moon.jpg
├── 8k_io.jpg
├── 8k_europa.jpg
├── 8k_ganymede.jpg
├── 8k_callisto.jpg
├── 8k_titan.jpg
└── 8k_stars_milky_way.jpg       # V7: equirectangular sky
```

Planet / dwarf-planet orbital elements live in `data/planets.json`; constellation lines in `data/constellations.json`. Both are loaded at start-up with a built-in fallback if missing or malformed.

> Public-domain 8K planet maps are available from **[Solar System Scope](https://www.solarsystemscope.com/textures/)**.

---

## 🧱 Architecture

```
Program.cs                entry point; configures NativeWindowSettings (4.5 Core)
SolarSystemWindow.cs      GameWindow: input, update loop, render orchestration
Renderer.cs               OpenGL resources & shader pipelines
                          (Sun, planets, clouds, orbits, rings, trails,
                           sky, axes, text, HDR/bloom post, lens flare)
Camera.cs                 yaw/pitch/distance orbital camera, mouse handling
Planet.cs                 planet data + JSON loader + built-in fallback
Moon.cs                   moon record (host, orbit radius, period, phase)
OrbitalMechanics.cs       Kepler solver, heliocentric → world-space scaling
AsteroidBelt.cs           8000-rock Kepler-solved instanced-quad cloud
Comet.cs                  Halley-like ellipse + ion/dust particle tail
Constellations.cs         skybox-anchored RA/Dec line overlay
SolarWind.cs              instanced-quad particle pool radiating from the Sun
SolarFlares.cs            instanced-quad eruption sprites that feed bloom
InstancedQuadParticles.cs shared instance VBO + draw helper for all particle systems
ShaderSources.cs          loads & caches `Resources/Shaders/*.glsl` files
BitmapFont.cs             SkiaSharp glyph atlas (Segoe UI/DejaVu/Arial fallback, RGBA8)
TextureManager.cs         texture loader + procedural / ring fallbacks
ShaderProgram.cs          thin GL shader compile/link helper
Resources/Shaders/*.glsl  every vertex/fragment shader as a copy-to-output file
data/planets.json         J2000 elements for planets + dwarfs
data/constellations.json  RA/Dec endpoints for constellation lines
```

### Rendering pipeline (per frame)

1. **Begin scene** — bind HDR (`RGBA16F`) FBO, clear colour + depth.
2. **Sky** — fullscreen quad samples the equirectangular Milky Way (or procedural starfield) via `uInvViewProj`.
3. **Constellations** (optional) — line segments at infinity.
4. **Orbit lines** + comet orbit (line strips per body).
5. **Trails** (optional) — per-planet fading line strips.
6. **Sun** — textured emissive sphere with HDR-boosted output, plus an additive halo billboard.
7. **Planets, Moon, Galileans, Titan, comet body** — Phong-lit textured spheres (with optional night-side emissive map).
8. **Cloud layer** — alpha-blended sphere over Earth.
9. **Saturn's ring** — alpha-blended textured quad.
10. **Asteroid belt** — additive `GL_POINTS`.
11. **Comet tail**, **solar wind**, **solar flares** — additive particles, depth-write off.
12. Optional debug axes.
13. **End scene + bloom** — bright-pass → 6 separable Gaussian blur passes (ping-pong between two half-res FBOs) → additive composite back to the default framebuffer.
14. Body labels (billboarded text), HUD overlay (date, speed, controls help, info panel, date-seek prompt).

### Solar wind details

- Pool of up to 6000 particles, each `(Pos, Vel, Life, MaxLife)`.
- Emission rate ≈ 1500 particles/s with fractional accumulator for smoothness, and a fixed-step (1/60 s) integrator so high `daysPerSecond` doesn't alias the motion.
- Spawn point: Sun surface (radius × 1.05).
- Direction: uniform on a sphere via inverse-CDF (`acos(1−2u)`).
- Speed: 35 world-units/s ± 40 % jitter; lifetime 6 s ± 40 %.
- GPU upload: live particles tightly packed into the shared `InstancedQuadParticles` instance VBO; the system draws a single 4-vertex quad via `glDrawArraysInstanced(TriangleStrip, 0, 4, count)`.
- The shared `particle.vert` reproduces the legacy `gl_PointSize` curve in clip space (`uPxBase / uPxMin / uPxMax / uViewportSize`), so quad sizes are now driver-independent.

---

## 📦 Dependencies

| Package | Version | Purpose |
|---|---|---|
| `OpenTK` | 4.8.2 | OpenGL bindings, windowing, input |
| `StbImageSharp` | 2.27.13 | JPG/PNG decoding for textures |
| `SkiaSharp` | 2.88.8 | Cross-platform font rasterisation (BitmapFont atlas) |
| `SkiaSharp.NativeAssets.Linux.NoDependencies` | 2.88.8 | Skia native blob for Linux runtimes |
| `System.Drawing.Common` | 9.0.0 | PNG encoding for the optional Windows-only `F12` screenshot |

---

## 🌍 The Eight Planets

| # | Name | Semi-major (AU) | Period (yr) | Tilt (°) | Notes |
|---|---|---|---|---|---|
| 1 | Mercury | 0.387 | 0.241 | 0.034 | — |
| 2 | Venus   | 0.723 | 0.615 | 177.4 | retrograde spin |
| 3 | Earth   | 1.000 | 1.000 | 23.44 | — |
| 4 | Mars    | 1.524 | 1.881 | 25.19 | — |
| 5 | Jupiter | 5.203 | 11.86 | 3.13  | — |
| 6 | Saturn  | 9.537 | 29.46 | 26.73 | rings |
| 7 | Uranus  | 19.19 | 84.01 | 97.77 | retrograde / sideways |
| 8 | Neptune | 30.07 | 164.8 | 28.32 | — |

Distances are non-linearly compressed to world space (`world = K · a^0.45`) so all eight bodies stay visible without dwarfing the inner planets.

---

## 📝 License

MIT for the source code. Planet textures are © their respective authors (see Solar System Scope license — generally CC-BY 4.0). This project is for educational and entertainment use.

---

## 🙏 Credits

- **Solar System Scope** — public-domain 8K planet maps.
- **OpenTK** team — first-class .NET OpenGL bindings.
- **StbImageSharp** — pure-managed image decoding.
- **NASA / JPL** — orbital element references.

### README

Below is the AI generated initial setup (which is already done in this github project).
You just need to drag Prefabs/OceanGenerator.prefab into scene and its ready!

# FFT Ocean — Unity 6 / URP 17+

Sea of Thieves-style ocean: a Tessendorf FFT simulation (JONSWAP spectrum, three
wavelength cascades) running fully on the GPU, with a URP surface shader doing
vertex displacement, analytic normals, whitecap foam from the displacement
Jacobian, subsurface "crest glow" scattering, refraction and reflection-probe
reflections. Same overall architecture as the SoT / Atlas GDC water talks.

## Files

- `OceanFFT.compute` — spectrum generation, time evolution, inverse FFT, map assembly
- `OceanSimulation.cs` — driver MonoBehaviour, publishes global shader textures
- `OceanSurface.shader` — `Kelobyte/Ocean` URP forward shader
- `OceanMeshBuilder.cs` — dense grid mesh with safe bounds

## Setup

1. Copy the four files into your project (any folder under Assets).
2. On your **URP asset**, enable **Depth Texture** and **Opaque Texture**
   (required for refraction and depth-based water color).
3. Create an empty GameObject, add `OceanMeshBuilder` (it adds MeshFilter/Renderer).
4. Create a material from shader `Kelobyte/Ocean` and assign it to the renderer.
5. Add `OceanSimulation` to the same (or any) GameObject and assign
   `OceanFFT.compute` to its **Ocean Compute** field.
6. Press Play (it also runs in edit mode via ExecuteAlways).

The simulation writes global textures, so every material using `Kelobyte/Ocean`
shares one simulation — you can tile several mesh objects for a larger area.

## Tuning

- **windSpeed / fetch** — overall sea state. 4 m/s = calm, 8 = SoT-ish rolling sea, 15+ = storm.
- **choppiness (Lambda)** — horizontal displacement sharpness. Above ~1.2 waves start folding and foam appears at crests.
- **foamDecay / _FoamBias / _FoamSharpness** — whitecap lifetime and coverage.
- **_SSSStrength / _SSSHeight** — the green translucent glow when looking through crests toward the sun. `_SSSHeight` should be near your typical crest height in metres.
- **lengthScale0/1/2** — cascade tile sizes. Keep ratios non-integer (250 / 47 / 9) so tiling never lines up.
- **_Cascade1Fade / _Cascade2Fade** — distances at which detail cascades fade out (kills shimmer/aliasing).
- **sizePower** — FFT resolution; 8 (256) is the quality/perf sweet spot, 9 (512) for hero shots.

## Performance

256³-cascade sim is roughly 1 ms of compute on a mid-range desktop GPU per frame
(4 packed IFFTs + assembly). The mesh is the other cost: 400×400 grid ≈ 320k tris.
For big view distances, place a few grid tiles at increasing scales around the
camera instead of one huge dense grid.

## Notes / limitations

- Water renders in the Transparent queue with ZWrite On so it can read the
  opaque color/depth textures. It doesn't cast shadows or write to the depth texture (intentional).
- Deep-water dispersion by default; lower `depth` for shallow-sea wave behavior.
- No tessellation/projected grid or underwater rendering — mesh density is your LOD.
- Buoyancy: sample the displacement maps (readback or reproduce the first cascade
  on CPU) if you need floating objects; ask me and I'll add a GPU-readback buoyancy sampler.

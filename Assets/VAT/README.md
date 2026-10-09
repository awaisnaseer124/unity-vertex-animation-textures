# Vertex Animation Textures (VAT) — learning kit

## The idea
A skinned mob costs CPU every frame: the Animator evaluates curves, bone transforms are updated,
and skinning runs (GPU skinning still needs a compute dispatch and a per-mob buffer). With 1000+ mobs
(Mob Control style), that's what kills mobile.

VAT moves all of that **offline**:

1. **Bake (Editor):** For every clip at N fps, pose the model and record the *final deformed position*
   of every vertex. Store them in a texture: one texel per vertex per frame.
2. **Play (GPU):** A plain static mesh. Each vertex knows its index (UV2). The vertex shader computes
   the current frame from `_Time`, fetches its position from the texture, and blends between 2 frames.

The CPU only says "play clip X starting at time T". After that it does **nothing** per mob, per frame.
Every mob of a type shares one mesh and one material, so they can be **GPU instanced**:
1000 mobs ≈ 1 draw call.

## Texture layout (`Shaders/VATCommon.hlsl`)
```
texelIndex = (clip.startFrame + frame) * vertexCount + vertexId
x = texelIndex % width,  y = texelIndex / width
```
Clips are stacked one after another. Format: RGBAHalf, point-filtered, no mips.
Normals get their own texture so lighting follows the animation.

## Files
| File | What to learn |
|---|---|
| `Editor/VATBaker.cs` | `SampleAnimation` → `BakeMesh` → write halves into a texture; merge submeshes into one, bake material colors into vertex colors; **bounds that cover all frames** (otherwise frustum culling pops). |
| `Shaders/VATCommon.hlsl` | Time → frame math, looping vs one-shot, `LOAD_TEXTURE2D_LOD` (texelFetch) in the vertex shader, instanced props. |
| `Shaders/VATLit.shader` | Cheap mobile lighting. ShadowCaster/DepthOnly passes must run the **same** VAT deformation. |
| `Runtime/VATAnimator.cs` | GameObject workflow: MaterialPropertyBlock set **only when the clip changes**. |
| `Runtime/VATInstancedRenderer.cs` | No GameObjects: `Graphics.RenderMeshInstanced`, 1023 per batch. |
| `Runtime/VATBenchmark.cs` | Skinned vs VAT GO vs VAT Instanced, with an on-device sweep that writes a CSV. |

## Workflow
1. `Tools > VAT > Bake All Monsters` (or `Tools > VAT > Baker Window` for one model with options).
   Output goes to `Assets/VAT/Baked/<Model>/`, including a ready `<Model>_VAT.prefab`.
2. `Tools > VAT > Create Benchmark Scene` → Play in Editor to check it looks right.
3. Build to the phone (scene 0). Tap **Run Sweep**. The CSV goes to `Application.persistentDataPath`
   and is printed to the log (`adb logcat -s Unity`).

## Baking any other model (e.g. Mixamo)
1. **Character:** download from Mixamo as FBX *with skin* (T-pose is fine). In Unity, under Rig, set
   Animation Type = **Humanoid** and Avatar = Create From This Model. Under Materials, use
   **Extract Textures** if the model looks white or pink.
2. **Animations:** download each one as FBX *Without Skin*, with **In Place** checked. Name the file after
   the clip (`Run.fbx`, `Idle.fbx`), because Mixamo calls every clip "mixamo.com" and the baker uses the file name.
   Under Rig, set Humanoid with Avatar = **Copy From Other Avatar** (the character's avatar).
3. `Tools > VAT > Baker Window`: drop the character into **Model**, then drop each animation FBX into
   **Add animations from**. Tick Loop for cyclic clips and press **Bake**.
4. Use `Assets/VAT/Baked/<Character>/<Character>_VAT.prefab`, or add the `_VAT_Data` asset to the
   benchmark's *Monsters* list.

Budget: texels = vertices × total frames, and the limit is 4096 × 4096 (~16.7M). A 20k-vertex Mixamo
character fits about 830 frames, roughly 35 s of animation at 24 fps. The window shows the size and warns before you bake.
Several textures (body, hair) become one submesh per texture, so 1 draw call per texture per batch.

## Reading the numbers
- FPS is capped by display refresh/vsync on phones. Compare **CPU main / render / GPU ms**.
- Skinned: CPU main grows quickly (Animator + transforms), plus skinning cost.
- VAT GO: animation is free, but each GameObject still costs transform sync + culling.
- VAT Instanced: CPU is close to flat, so the GPU (vertex count × instances, shadows) becomes the limit.
- Toggle **Shadows**: the shadow pass re-runs the VAT vertex shader for every mob.
- Toggle **Move** off to separate "animating" cost from "moving transforms" cost.

## Trade-offs / limits
- No blending between clips (a transition is a pop; hide it with fast clips or bake transition clips).
- No IK, ragdolls, or attachments on bones (unless you bake bone matrices, which is "bone VAT", a bigger topic).
- Memory = verts × frames × 8 bytes × 2 textures. Low-poly mobs are ideal; keep the texture ≤ 4096.
- Half precision: fine for ~human-scale models; large models drift (use RGBAFloat or store offsets).
- `_Time.y` loses precision after many hours; reset by level loads (it's time since level load).
- Further optimizations: pack normals into RGBA32 or octahedral, lower bake fps for distant mobs,
  per-instance culling in a compute shader + `RenderMeshIndirect`, LOD meshes baked separately.

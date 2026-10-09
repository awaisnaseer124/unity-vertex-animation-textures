using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace VAT.Editor
{
    /// Bakes skinned animation into Vertex Animation Textures.
    ///
    /// For every clip, at a fixed fps:
    ///   1. pose a hidden instance of the model at that time (PlayableGraph; Generic or Humanoid rigs)
    ///   2. BakeMesh() every SkinnedMeshRenderer -> deformed vertex positions/normals
    ///   3. write one texel per vertex into a position texture and a normal texture
    /// The output mesh gets each vertex's index in UV2, which the shader uses to find its texels.
    public static class VATBaker
    {
        public const string ShaderName = "VAT/Simple Lit";
        public const string DefaultOutputFolder = "Assets/VAT/Baked";
        const int MaxTextureSize = 4096;      // safe on practically every mobile GPU
        const int PreferredWidth = 2048;

        public class ClipSettings
        {
            public AnimationClip clip;
            public string name;
            public bool loop;
            public bool include = true;
        }

        // A renderer that contributes vertices to the combined baked mesh.
        class Part
        {
            public Renderer renderer;
            public Mesh sourceMesh;
            public int vertexOffset;
            public Mesh bakeBuffer; // reused for SkinnedMeshRenderer.BakeMesh
        }

        // ------------------------------------------------------------------ helpers for callers

        /// All clips inside an asset: a model FBX (with or without skin, e.g. Mixamo downloads) or a .anim file.
        public static List<AnimationClip> LoadClips(Object asset)
        {
            if (asset is AnimationClip single) return new List<AnimationClip> { single };
            string path = AssetDatabase.GetAssetPath(asset);
            return AssetDatabase.LoadAllAssetRepresentationsAtPath(path)
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"))
                .OrderBy(c => c.name)
                .ToList();
        }

        /// "SkeletonArmature|Skeleton_Running" -> "Running".
        /// Mixamo names every clip "mixamo.com", so those are named after their file ("Running.fbx" -> "Running").
        public static string ClipDisplayName(AnimationClip clip)
        {
            string n = clip.name;
            if (n.Contains("mixamo.com") || n == "Take 001")
                return Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(clip));
            int bar = n.LastIndexOf('|');
            if (bar >= 0) n = n.Substring(bar + 1);
            int us = n.IndexOf('_');
            if (us >= 0 && us < n.Length - 1) n = n.Substring(us + 1);
            return n;
        }

        public static bool GuessLoop(AnimationClip clip)
        {
            string n = (clip.name + " " + ClipDisplayName(clip)).ToLowerInvariant();
            return clip.isLooping || n.Contains("idle") || n.Contains("walk") || n.Contains("run") || n.Contains("fly") || n.Contains("loop");
        }

        public static ClipSettings MakeClipSettings(AnimationClip c) =>
            new() { clip = c, name = ClipDisplayName(c), loop = GuessLoop(c) };

        public static List<ClipSettings> DefaultClipSettings(Object asset) => LoadClips(asset).Select(MakeClipSettings).ToList();

        /// Sets "Loop Time" on each clip's importer (or .anim settings) so the original Animator version
        /// loops too, which keeps the Skinned-vs-VAT comparison fair. Re-resolves clips that got reimported.
        public static void ApplyLoopFlags(List<ClipSettings> clips)
        {
            foreach (var group in clips.GroupBy(c => AssetDatabase.GetAssetPath(c.clip)).ToList())
            {
                string path = group.Key;
                if (AssetImporter.GetAtPath(path) is ModelImporter importer)
                {
                    var loopByName = group.GroupBy(c => c.clip.name).ToDictionary(g => g.Key, g => g.First().loop);
                    var anims = importer.clipAnimations;
                    if (anims == null || anims.Length == 0) anims = importer.defaultClipAnimations;

                    bool changed = false;
                    foreach (var a in anims)
                    {
                        if (loopByName.TryGetValue(a.name, out bool loop) && a.loopTime != loop)
                        {
                            a.loopTime = loop;
                            changed = true;
                        }
                    }
                    if (!changed) continue;

                    importer.clipAnimations = anims;
                    importer.SaveAndReimport();
                    // Reimport replaces the clip objects; find them again by name in the same file.
                    var fresh = AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<AnimationClip>()
                        .GroupBy(c => c.name).ToDictionary(g => g.Key, g => g.First());
                    foreach (var c in group)
                        if (fresh.TryGetValue(c.clip.name, out var clip)) c.clip = clip;
                }
                else
                {
                    foreach (var c in group)
                    {
                        var s = AnimationUtility.GetAnimationClipSettings(c.clip);
                        if (s.loopTime == c.loop) continue;
                        s.loopTime = c.loop;
                        AnimationUtility.SetAnimationClipSettings(c.clip, s);
                        EditorUtility.SetDirty(c.clip);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ bake

        public static VATAnimationData Bake(GameObject model, List<ClipSettings> clipSettings, float fps, string outputFolder = DefaultOutputFolder)
        {
            var clips = clipSettings.Where(c => c.include && c.clip != null).ToList();
            if (clips.Count == 0) throw new ArgumentException("No clips selected.");

            // Unique names: FindClip("Run") must be unambiguous.
            var used = new HashSet<string>();
            foreach (var c in clips)
            {
                string baseName = string.IsNullOrWhiteSpace(c.name) ? ClipDisplayName(c.clip) : c.name;
                string n = baseName;
                for (int i = 2; !used.Add(n); i++) n = $"{baseName}_{i}";
                c.name = n;
            }

            string modelPath = AssetDatabase.GetAssetPath(model);
            ApplyLoopFlags(clips);
            model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath); // may have been reimported

            var shader = Shader.Find(ShaderName);
            if (shader == null) throw new Exception($"Shader '{ShaderName}' not found.");

            string modelName = model.name;
            string folder = $"{outputFolder}/{modelName}";
            EnsureFolder(folder);

            // Hidden instance at the origin. Its root rotation/scale (e.g. Blender's -90° X) are kept, so the
            // VAT prefab (identity transform) looks exactly like the original model dropped into a scene.
            var go = (GameObject)Object.Instantiate(model);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.transform.position = Vector3.zero;
            var sampler = new PoseSampler(go);

            try
            {
                var parts = CollectParts(go, out int vertexCount);
                if (vertexCount == 0) throw new Exception($"{modelName}: no mesh renderers found.");

                // ---- frame table
                var vatClips = new VATClip[clips.Count];
                int totalFrames = 0;
                for (int i = 0; i < clips.Count; i++)
                {
                    var c = clips[i];
                    float len = Mathf.Max(c.clip.length, 1f / fps);
                    // Looping clips skip the last frame (it equals the first); one-shots keep it.
                    int frames = c.loop ? Mathf.Max(1, Mathf.RoundToInt(len * fps))
                                        : Mathf.RoundToInt(len * fps) + 1;
                    vatClips[i] = new VATClip
                    {
                        name = c.name,
                        startFrame = totalFrames,
                        frameCount = frames,
                        // exact rate so playback length == clip length
                        fps = c.loop ? frames / len : (frames - 1) / len,
                        loop = c.loop,
                    };
                    if (!c.loop && frames == 1) vatClips[i].fps = fps;
                    totalFrames += frames;
                }

                // ---- texture size: texels laid out linearly, wrapped at `width`
                long texels = (long)totalFrames * vertexCount;
                int width = (int)Math.Min(texels, PreferredWidth);
                int height = (int)((texels + width - 1) / width);
                if (height > MaxTextureSize) { width = MaxTextureSize; height = (int)((texels + width - 1) / width); }
                if (height > MaxTextureSize)
                    Debug.LogWarning($"{modelName}: VAT texture {width}x{height} exceeds {MaxTextureSize}. Lower the fps or bake fewer clips.");

                var posData = new ushort[width * height * 4];
                var nrmData = new ushort[width * height * 4];
                var positions = new Vector3[vertexCount];
                var normals = new Vector3[vertexCount];
                var bounds = new Bounds();
                bool boundsInit = false;

                // ---- sample every frame
                for (int ci = 0; ci < clips.Count; ci++)
                {
                    var clip = clips[ci].clip;
                    var vc = vatClips[ci];
                    for (int f = 0; f < vc.frameCount; f++)
                    {
                        if (EditorUtility.DisplayCancelableProgressBar("Baking VAT", $"{modelName} / {vc.name} ({f + 1}/{vc.frameCount})",
                                (float)(vc.startFrame + f) / totalFrames))
                            throw new OperationCanceledException();

                        float t = Mathf.Min(f / vc.fps, clip.length);
                        sampler.Sample(clip, t);
                        SnapshotParts(parts, positions, normals);

                        int texelBase = (vc.startFrame + f) * vertexCount;
                        for (int v = 0; v < vertexCount; v++)
                        {
                            WriteHalf4(posData, texelBase + v, positions[v]);
                            WriteHalf4(nrmData, texelBase + v, normals[v]);
                            if (!boundsInit) { bounds = new Bounds(positions[v], Vector3.zero); boundsInit = true; }
                            else bounds.Encapsulate(positions[v]);
                        }
                    }
                }

                // ---- rest pose for the mesh asset: first frame of the default clip
                int defaultClip = PickDefaultClip(vatClips);
                sampler.Sample(clips[defaultClip].clip, 0f);
                SnapshotParts(parts, positions, normals);

                // ---- write assets
                var posTex = WriteTexture($"{folder}/{modelName}_VAT_Positions.asset", width, height, posData);
                var nrmTex = WriteTexture($"{folder}/{modelName}_VAT_Normals.asset", width, height, nrmData);

                var mesh = LoadOrCreate($"{folder}/{modelName}_VAT_Mesh.asset", () => new Mesh());
                Texture[] baseMaps = BuildMesh(mesh, parts, positions, normals, bounds);
                mesh.name = $"{modelName}_VAT_Mesh";
                EditorUtility.SetDirty(mesh);

                var data = LoadOrCreate($"{folder}/{modelName}_VAT_Data.asset", ScriptableObject.CreateInstance<VATAnimationData>);
                data.mesh = mesh;
                data.positionTexture = posTex;
                data.normalTexture = nrmTex;
                data.vertexCount = vertexCount;
                data.clips = vatClips;
                data.defaultClip = defaultClip;
                data.sourcePrefab = model;
                data.sourceClips = clips.Select(c => c.clip).ToArray();

                // One material per submesh; they only differ by _BaseMap.
                var mats = new Material[baseMaps.Length];
                for (int s = 0; s < baseMaps.Length; s++)
                {
                    string matPath = s == 0 ? $"{folder}/{modelName}_VAT.mat" : $"{folder}/{modelName}_VAT_{s}.mat";
                    var mat = LoadOrCreate(matPath, () => new Material(shader));
                    mat.shader = shader;
                    mat.SetTexture("_VATPositionTex", posTex);
                    mat.SetTexture("_VATNormalTex", nrmTex);
                    mat.SetVector("_VATTexSize", new Vector4(width, height, vertexCount, 0));
                    mat.SetVector("_VATClip", data.GetClipParams(defaultClip));
                    mat.SetVector("_VATPlayback", VATAnimationData.GetPlaybackParams(0, 1));
                    mat.SetTexture("_BaseMap", baseMaps[s]);
                    mat.enableInstancing = true; // required for Graphics.RenderMeshInstanced and MPB batching
                    EditorUtility.SetDirty(mat);
                    mats[s] = mat;
                }

                data.material = mats[0];
                data.materials = mats;
                EditorUtility.SetDirty(data);

                CreatePrefab($"{folder}/{modelName}_VAT.prefab", data);

                AssetDatabase.SaveAssets();
                Debug.Log($"[VAT] Baked {modelName}: {vertexCount} verts, {mats.Length} submesh(es), {clips.Count} clips, {totalFrames} frames -> " +
                          $"{width}x{height} RGBAHalf x2 ({width * height * 8 * 2 / 1024f:0} KB)", data);
                return data;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                sampler.Dispose();
                Object.DestroyImmediate(go);
            }
        }

        /// Poses the hidden model at time t of a clip.
        /// Uses a PlayableGraph (like Timeline preview), which works for Generic AND Humanoid rigs.
        /// AnimationClip.SampleAnimation does not handle humanoid (retargeted, e.g. Mixamo) clips reliably.
        sealed class PoseSampler : IDisposable
        {
            PlayableGraph _graph;
            AnimationPlayableOutput _output;
            AnimationClipPlayable _playable;
            AnimationClip _current;

            public PoseSampler(GameObject go)
            {
                if (!go.TryGetComponent(out Animator animator)) animator = go.AddComponent<Animator>();
                animator.enabled = true;
                animator.applyRootMotion = false; // keep the mob in place; your game code moves it
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                _graph = PlayableGraph.Create("VAT Bake");
                _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                _output = AnimationPlayableOutput.Create(_graph, "Pose", animator);
            }

            public void Sample(AnimationClip clip, float time)
            {
                if (clip != _current)
                {
                    if (_playable.IsValid()) _playable.Destroy();
                    _playable = AnimationClipPlayable.Create(_graph, clip);
                    _playable.SetApplyFootIK(false);
                    _output.SetSourcePlayable(_playable);
                    _current = clip;
                }
                _playable.SetTime(time);
                _playable.SetTime(time); // twice: also sets the "previous" time, so nothing is interpolated
                _graph.Evaluate();
            }

            public void Dispose()
            {
                if (_graph.IsValid()) _graph.Destroy();
            }
        }

        static int PickDefaultClip(VATClip[] clips)
        {
            foreach (var key in new[] { "run", "walk", "fly", "idle" })
                for (int i = 0; i < clips.Length; i++)
                    if (clips[i].name.ToLowerInvariant().Contains(key)) return i;
            return 0;
        }

        static List<Part> CollectParts(GameObject root, out int vertexCount)
        {
            var parts = new List<Part>();
            vertexCount = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                Mesh m = r switch
                {
                    SkinnedMeshRenderer smr => smr.sharedMesh,
                    MeshRenderer mr => mr.GetComponent<MeshFilter>()?.sharedMesh,
                    _ => null,
                };
                if (m == null || !r.enabled) continue;
                parts.Add(new Part { renderer = r, sourceMesh = m, vertexOffset = vertexCount, bakeBuffer = new Mesh() });
                vertexCount += m.vertexCount;
            }
            return parts;
        }

        // Current deformed pose of all parts, in root (= world, root is at the origin) space.
        static void SnapshotParts(List<Part> parts, Vector3[] positions, Vector3[] normals)
        {
            var tmpV = new List<Vector3>();
            var tmpN = new List<Vector3>();
            foreach (var p in parts)
            {
                // useScale: true -> vertices stay in the renderer's local space, so the full
                // localToWorldMatrix applies (Blender FBX meshes often sit under a 100x scale + -90° X).
                if (p.renderer is SkinnedMeshRenderer smr)
                {
                    smr.BakeMesh(p.bakeBuffer, true);
                    p.bakeBuffer.GetVertices(tmpV);
                    p.bakeBuffer.GetNormals(tmpN);
                }
                else
                {
                    p.sourceMesh.GetVertices(tmpV);
                    p.sourceMesh.GetNormals(tmpN);
                }
                Matrix4x4 m = p.renderer.transform.localToWorldMatrix;
                var nm = m.inverse.transpose;
                for (int i = 0; i < tmpV.Count; i++)
                {
                    positions[p.vertexOffset + i] = m.MultiplyPoint3x4(tmpV[i]);
                    normals[p.vertexOffset + i] = i < tmpN.Count ? nm.MultiplyVector(tmpN[i]).normalized : Vector3.up;
                }
            }
        }

        /// Combines all parts into one mesh. Material colors are baked into vertex colors, so materials only
        /// differ by texture: submeshes are merged per distinct texture. Flat-colored models (like the monster
        /// pack) end up with ONE submesh = one draw call per mob; a textured Mixamo character with
        /// body + hair textures gets two. Returns the texture of each output submesh.
        static Texture[] BuildMesh(Mesh mesh, List<Part> parts, Vector3[] positions, Vector3[] normals, Bounds bounds)
        {
            int vc = positions.Length;
            var uv0 = new Vector2[vc];
            var ids = new Vector2[vc];
            var colors = new Color[vc];
            var groupTextures = new List<Texture>();
            var groupTris = new List<List<int>>();
            bool linear = PlayerSettings.colorSpace == ColorSpace.Linear;

            List<int> TrisFor(Texture tex)
            {
                int g = groupTextures.IndexOf(tex);
                if (g < 0)
                {
                    groupTextures.Add(tex);
                    groupTris.Add(new List<int>());
                    g = groupTextures.Count - 1;
                }
                return groupTris[g];
            }

            var tmpUV = new List<Vector2>();
            var tmpCol = new List<Color>();
            foreach (var p in parts)
            {
                var m = p.sourceMesh;
                m.GetUVs(0, tmpUV);
                m.GetColors(tmpCol);
                var mats = p.renderer.sharedMaterials;

                for (int i = 0; i < m.vertexCount; i++)
                {
                    int v = p.vertexOffset + i;
                    uv0[v] = i < tmpUV.Count ? tmpUV[i] : Vector2.zero;
                    ids[v] = new Vector2(v, 0);
                    colors[v] = i < tmpCol.Count ? tmpCol[i] : Color.white;
                }

                for (int s = 0; s < m.subMeshCount; s++)
                {
                    var mat = s < mats.Length ? mats[s] : null;
                    Color c = Color.white;
                    Texture tex = null;
                    if (mat != null)
                    {
                        c = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : mat.HasProperty("_Color") ? mat.color : Color.white;
                        tex = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : mat.HasProperty("_MainTex") ? mat.mainTexture : null;
                    }
                    if (linear) c = c.linear; // vertex colors are not converted by Unity, material colors are

                    var tris = TrisFor(tex);
                    var sub = m.GetTriangles(s);
                    foreach (int idx in sub)
                    {
                        colors[p.vertexOffset + idx] *= c;
                        tris.Add(p.vertexOffset + idx);
                    }
                }
            }
            // A vertex shared by two submeshes would be multiplied twice; rare in practice.

            mesh.Clear();
            mesh.indexFormat = vc > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(2, ids);
            mesh.SetColors(colors);
            mesh.subMeshCount = groupTris.Count;
            for (int g = 0; g < groupTris.Count; g++)
                mesh.SetTriangles(groupTris[g], g, false);
            bounds.Expand(0.1f);
            mesh.bounds = bounds; // covers EVERY frame, otherwise animated parts get frustum-culled
            mesh.UploadMeshData(false);
            return groupTextures.ToArray();
        }

        static void CreatePrefab(string path, VATAnimationData data)
        {
            var go = new GameObject(Path.GetFileNameWithoutExtension(path));
            try
            {
                go.AddComponent<MeshFilter>().sharedMesh = data.mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = data.Materials;
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                go.AddComponent<VATAnimator>().data = data;
                PrefabUtility.SaveAsPrefabAsset(go, path);
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ------------------------------------------------------------------ low level

        static void WriteHalf4(ushort[] dst, int texel, Vector3 v)
        {
            int i = texel * 4;
            dst[i] = Mathf.FloatToHalf(v.x);
            dst[i + 1] = Mathf.FloatToHalf(v.y);
            dst[i + 2] = Mathf.FloatToHalf(v.z);
            dst[i + 3] = Mathf.FloatToHalf(1f);
        }

        static Texture2D WriteTexture(string path, int width, int height, ushort[] data)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null)
            {
                tex = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);
                AssetDatabase.CreateAsset(tex, path);
            }
            else if (tex.width != width || tex.height != height || tex.format != TextureFormat.RGBAHalf)
            {
                tex.Reinitialize(width, height, TextureFormat.RGBAHalf, false);
            }
            tex.name = Path.GetFileNameWithoutExtension(path);
            tex.filterMode = FilterMode.Point;  // we fetch exact texels; never filter
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 0;
            tex.SetPixelData(data, 0);
            tex.Apply(false, false);
            EditorUtility.SetDirty(tex);
            return tex;
        }

        static T LoadOrCreate<T>(string path, Func<T> create) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            asset = create();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        public static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}

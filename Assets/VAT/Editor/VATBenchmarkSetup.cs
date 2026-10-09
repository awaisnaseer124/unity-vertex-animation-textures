using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace VAT.Editor
{
    public static class VATBenchmarkSetup
    {
        const string ScenePath = "Assets/VAT/Scenes/VATBenchmark.unity";
        const string MaterialsFolder = "Assets/VAT/Scenes/Materials";

        static readonly Color Sky = new(0.62f, 0.80f, 0.96f);

        [MenuItem("Tools/VAT/Create Benchmark Scene", priority = 20)]
        static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var monsters = FindBakedData();
            if (monsters.Length == 0)
            {
                EditorApplication.ExecuteMenuItem("Tools/VAT/Bake All Monsters (Animated Monster Pack)");
                monsters = FindBakedData();
            }
            if (monsters.Length == 0) { Debug.LogError("[VAT] Nothing baked; open Tools > VAT > Baker Window."); return; }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional;
            var cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            var bench = new GameObject("VAT Benchmark").AddComponent<VATBenchmark>();
            bench.monsters = monsters;
            bench.mainLight = light;

            Dress(bench, light, cam);

            VATBaker.EnsureFolder("Assets/VAT/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            // Put the benchmark first in the build so a device build opens it directly.
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            // Needed for CPU/GPU timings via FrameTimingManager in player builds.
            PlayerSettings.enableFrameTimingStats = true;

            Debug.Log("[VAT] Benchmark scene created. Press Play, or build to your phone (it's scene 0 in Build Profiles).");
        }

        /// Makes the open benchmark scene presentable: track, rails, ground, camera, light, fog, formation.
        [MenuItem("Tools/VAT/Dress Benchmark Scene", priority = 21)]
        static void DressOpenScene()
        {
            var bench = Object.FindAnyObjectByType<VATBenchmark>();
            if (bench == null) { Debug.LogError("[VAT] Open the benchmark scene first."); return; }
            var light = bench.mainLight != null ? bench.mainLight : RenderSettings.sun;
            var cam = Camera.main;
            if (light == null || cam == null) { Debug.LogError("[VAT] Scene needs a directional light and a MainCamera."); return; }
            Dress(bench, light, cam);
            EditorSceneManager.MarkSceneDirty(bench.gameObject.scene);
            Debug.Log("[VAT] Benchmark scene dressed. Save the scene to keep it.");
        }

        public static void Dress(VATBenchmark bench, Light light, Camera cam)
        {
            Undo.RecordObjects(new Object[] { bench, light, light.transform, cam, cam.transform }, "Dress VAT Benchmark");
            VATBaker.EnsureFolder(MaterialsFolder);

            // ---- crowd: army lanes running toward the camera
            bench.area = new Vector2(40f, 80f);
            bench.moveDirection = Vector3.back;
            bench.laneFormation = true;
            bench.laneGap = 2.5f;
            bench.formationJitter = 0.75f;
            bench.edgeFadeDistance = 5f;
            bench.moveSpeed = 4f;
            bench.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            // ---- lighting: warm key light + cool flat ambient (flat ambient = SampleSH works without baking)
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.95f, 0.86f);
            light.intensity = 1.35f;
            light.shadows = LightShadows.None; // matches the published numbers; toggle in the HUD
            light.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
            RenderSettings.sun = light;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.56f, 0.62f, 0.72f);
            RenderSettings.skybox = null;

            // ---- fog fades the far end of the field into the sky
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Sky;
            RenderSettings.fogStartDistance = 75f;
            RenderSettings.fogEndDistance = 170f;

            // ---- camera: in front of the army, looking down the track. The bottom of the frame cuts the
            // track just past the front edge, so mobs shrink out off-screen instead of in view.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Sky;
            cam.fieldOfView = 45f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 250f;
            cam.transform.position = new Vector3(0f, 28f, -58f);
            cam.transform.LookAt(new Vector3(0f, 0f, 2f));

            // ---- environment
            var env = GetOrCreate("Environment", null);
            var oldGround = GameObject.Find("Ground");
            if (oldGround != null && oldGround.transform.parent != env.transform) Undo.DestroyObjectImmediate(oldGround);

            float trackWidth = bench.area.x + 4f;
            float trackLength = bench.area.y + 120f; // runs out of view at both ends
            float trackCenterZ = 20f;

            var ground = Primitive("Ground", PrimitiveType.Plane, env.transform);
            ground.transform.SetPositionAndRotation(new Vector3(0f, -0.05f, trackCenterZ), Quaternion.identity);
            ground.transform.localScale = new Vector3(60f, 1f, 60f);
            ground.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial("VAT_Ground", new Color(0.42f, 0.70f, 0.45f));

            var track = Primitive("Track", PrimitiveType.Plane, env.transform);
            track.transform.SetPositionAndRotation(new Vector3(0f, 0f, trackCenterZ), Quaternion.identity);
            track.transform.localScale = new Vector3(trackWidth / 10f, 1f, trackLength / 10f);
            track.GetComponent<MeshRenderer>().sharedMaterial = TrackMaterial(trackWidth, trackLength);

            var railMat = FlatMaterial("VAT_Rail", new Color(1f, 0.55f, 0.18f));
            for (int side = -1; side <= 1; side += 2)
            {
                var rail = Primitive(side < 0 ? "Rail Left" : "Rail Right", PrimitiveType.Cube, env.transform);
                rail.transform.SetPositionAndRotation(new Vector3(side * (trackWidth * 0.5f + 0.5f), 0.4f, trackCenterZ), Quaternion.identity);
                rail.transform.localScale = new Vector3(1f, 0.8f, trackLength);
                rail.GetComponent<MeshRenderer>().sharedMaterial = railMat;
            }

            EditorUtility.SetDirty(bench);
        }

        // ------------------------------------------------------------------ helpers

        static GameObject GetOrCreate(string name, Transform parent)
        {
            var existing = parent != null ? parent.Find(name)?.gameObject : GameObject.Find(name);
            if (existing != null) return existing;
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            return go;
        }

        static GameObject Primitive(string name, PrimitiveType type, Transform parent)
        {
            var existing = parent.Find(name);
            if (existing != null) return existing.gameObject;
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            Object.DestroyImmediate(go.GetComponent<Collider>()); // visuals only
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            return go;
        }

        static Material FlatMaterial(string name, Color color)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", 0.1f);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material TrackMaterial(float width, float length)
        {
            string path = $"{MaterialsFolder}/VAT_Track.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = Shader.Find("Universal Render Pipeline/Lit");
            mat.SetTexture("_BaseMap", GridTexture());
            mat.SetColor("_BaseColor", new Color(0.93f, 0.90f, 0.84f));
            mat.SetTextureScale("_BaseMap", new Vector2(width / 4f, length / 4f)); // one grid tile per 4 m
            mat.SetFloat("_Smoothness", 0.15f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// A simple tileable grid texture, generated so the project needs no third-party textures.
        static Texture2D GridTexture()
        {
            string path = $"{MaterialsFolder}/VAT_Grid.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int size = 256, line = 3, sub = 1;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var bg = new Color(1f, 1f, 1f);
            var major = new Color(0.72f, 0.72f, 0.72f);
            var minor = new Color(0.86f, 0.86f, 0.86f);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool edge = x < line || y < line;                          // tile border (tiles to a full grid)
                bool half = Mathf.Abs(x - size / 2) < sub || Mathf.Abs(y - size / 2) < sub; // 2 m sub-lines
                tex.SetPixel(x, y, edge ? major : half ? minor : bg);
            }
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.anisoLevel = 8;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static VATAnimationData[] FindBakedData() =>
            AssetDatabase.FindAssets("t:VATAnimationData")
                .Select(g => AssetDatabase.LoadAssetAtPath<VATAnimationData>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(d => d != null && d.sourcePrefab != null)
                .OrderBy(d => d.name)
                .ToArray();
    }
}

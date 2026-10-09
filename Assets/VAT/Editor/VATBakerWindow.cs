using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VAT.Editor
{
    public class VATBakerWindow : EditorWindow
    {
        const string MonsterFolder = "Assets/Art/Animated Monster Pack/FBX";
        const int MaxTexels = 4096 * 4096;

        GameObject _model;
        float _fps = 30f;
        string _output = VATBaker.DefaultOutputFolder;
        List<VATBaker.ClipSettings> _clips = new();
        Vector2 _scroll;
        bool _showMixamoHelp;

        [MenuItem("Tools/VAT/Baker Window", priority = 0)]
        static void Open() => GetWindow<VATBakerWindow>("VAT Baker");

        [MenuItem("Tools/VAT/Bake All Monsters (Animated Monster Pack)", priority = 1)]
        static void BakeAllMonsters()
        {
            var guids = AssetDatabase.FindAssets("t:Model", new[] { MonsterFolder });
            if (guids.Length == 0) { Debug.LogError($"[VAT] No models found in {MonsterFolder}"); return; }
            foreach (var guid in guids)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                VATBaker.Bake(model, VATBaker.DefaultClipSettings(model), 30f);
            }
        }

        void OnEnable()
        {
            if (Selection.activeObject is GameObject go && PrefabUtility.GetPrefabAssetType(go) == PrefabAssetType.Model)
                SetModel(go);
        }

        void SetModel(GameObject model)
        {
            _model = model;
            _clips = model ? VATBaker.DefaultClipSettings(model) : new List<VATBaker.ClipSettings>();
        }

        void AddAnimations(Object asset)
        {
            foreach (var clip in VATBaker.LoadClips(asset))
                if (_clips.All(c => c.clip != clip))
                    _clips.Add(VATBaker.MakeClipSettings(clip));
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "1. Model: the skinned character (FBX).\n" +
                "2. Clips inside it are listed. Drop extra animation FBX/.anim files below (e.g. Mixamo 'Without Skin' downloads).\n" +
                "3. Bake: outputs mesh, textures, material(s), data asset and a ready prefab.",
                MessageType.Info);

            var model = (GameObject)EditorGUILayout.ObjectField("Model (FBX)", _model, typeof(GameObject), false);
            if (model != _model) SetModel(model);

            _fps = EditorGUILayout.Slider("Sample FPS", _fps, 5f, 60f);
            _output = EditorGUILayout.TextField("Output Folder", _output);

            _showMixamoHelp = EditorGUILayout.Foldout(_showMixamoHelp, "Mixamo / Humanoid setup", true);
            if (_showMixamoHelp)
                EditorGUILayout.HelpBox(
                    "• Character FBX: Rig > Animation Type = Humanoid, Avatar = Create From This Model.\n" +
                    "• Each animation FBX: Humanoid, Avatar = Copy From Other Avatar (the character's).\n" +
                    "• Download animations with 'In Place' checked (or the mob drifts inside its clip).\n" +
                    "• Textures pink/missing? Character FBX > Materials > Extract Textures.\n" +
                    "• Mixamo characters are 10k–50k verts: use 15–24 fps and only the clips you need.",
                    MessageType.None);

            if (_model == null) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Clips", EditorStyles.boldLabel);

            var dropped = EditorGUILayout.ObjectField("Add animations from", null, typeof(Object), false);
            if (dropped is GameObject || dropped is AnimationClip) AddAnimations(dropped);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(260));
            VATBaker.ClipSettings remove = null;
            foreach (var c in _clips)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    c.include = EditorGUILayout.Toggle(c.include, GUILayout.Width(18));
                    c.name = EditorGUILayout.TextField(c.name);
                    c.loop = EditorGUILayout.ToggleLeft("Loop", c.loop, GUILayout.Width(50));
                    EditorGUILayout.LabelField($"{c.clip.length:0.00}s", GUILayout.Width(45));
                    if (GUILayout.Button("✕", GUILayout.Width(22))) remove = c;
                }
            }
            if (remove != null) _clips.Remove(remove);
            EditorGUILayout.EndScrollView();

            int verts = _model.GetComponentsInChildren<SkinnedMeshRenderer>().Sum(s => s.sharedMesh ? s.sharedMesh.vertexCount : 0)
                      + _model.GetComponentsInChildren<MeshFilter>().Sum(f => f.sharedMesh ? f.sharedMesh.vertexCount : 0);
            int frames = _clips.Where(c => c.include).Sum(c => Mathf.RoundToInt(c.clip.length * _fps) + 1);
            long texels = (long)verts * frames;
            EditorGUILayout.LabelField($"{verts:N0} verts x {frames} frames = {texels:N0} texels ≈ {texels * 16 / (1024f * 1024f):0.0} MB (pos + normal)");
            if (texels > MaxTexels)
                EditorGUILayout.HelpBox($"Too big for a 4096x4096 texture (max {MaxTexels / Mathf.Max(verts, 1)} frames for this mesh). " +
                                        "Lower the FPS, shorten/drop clips, or use a lower-poly model.", MessageType.Error);
            else if (texels > MaxTexels / 4)
                EditorGUILayout.HelpBox("Large texture: fine for a few hero types, heavy if you have many mob types on mobile.", MessageType.Warning);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!_clips.Any(c => c.include) || texels > MaxTexels))
            {
                if (GUILayout.Button("Bake", GUILayout.Height(32)))
                {
                    var data = VATBaker.Bake(_model, _clips, _fps, _output);
                    EditorGUIUtility.PingObject(data);
                    GUIUtility.ExitGUI();
                }
            }
        }
    }
}

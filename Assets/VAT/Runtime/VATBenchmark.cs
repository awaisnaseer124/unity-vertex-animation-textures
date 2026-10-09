using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;

namespace VAT
{
    /// Spawns a crowd of mobs and compares three ways of animating them:
    ///   Skinned        - original model, SkinnedMeshRenderer + Animator (PlayableGraph per mob)
    ///   VAT GameObject - baked prefab, MeshRenderer + VATAnimator (GPU instanced via MaterialPropertyBlock)
    ///   VAT Instanced  - no GameObjects, Graphics.RenderMeshInstanced in batches of 1023
    /// Use "Run Sweep" on the device; results are shown on screen and saved as CSV.
    public class VATBenchmark : MonoBehaviour
    {
        public enum Mode { Skinned, VATGameObjects, VATInstanced }

        static readonly string[] ModeNames = { "Skinned", "VAT GO", "VAT" };

        Mode[] VisibleModes => showVatGameObjectsMode
            ? new[] { Mode.Skinned, Mode.VATGameObjects, Mode.VATInstanced }
            : new[] { Mode.Skinned, Mode.VATInstanced };
        static readonly int[] CountSteps = { 50, 100, 250, 500, 1000, 2000, 4000, 8000 };

        [Header("Content")]
        public VATAnimationData[] monsters;
        public Light mainLight;

        [Header("Crowd")]
        public Mode mode = Mode.VATInstanced;
        [Tooltip("Also offer the in-between 'VAT on GameObjects' mode. Off = a clean Skinned vs VAT comparison.")]
        public bool showVatGameObjectsMode;
        public int count = 500;
        public Vector2 area = new(40f, 80f);        // x = width, y = length (along moveDirection)
        [Tooltip("Mobs run this way and face it. Vector3.back = toward a camera placed at -Z.")]
        public Vector3 moveDirection = Vector3.back;
        public float moveSpeed = 4f;
        public bool move = true;

        [Header("Presentation")]
        [Tooltip("One lane per monster type in an army grid. Off = random scatter.")]
        public bool laneFormation = true;
        public float laneGap = 2f;
        [Range(0f, 1f)] public float formationJitter = 0.35f;
        [Tooltip("Mobs scale in/out over this distance at both ends of the field instead of popping when they wrap.")]
        public float edgeFadeDistance = 5f;

        [Header("Sweep")]
        public int[] sweepCounts = { 100, 250, 500, 1000, 2000, 4000 };
        public float warmupSeconds = 2f;
        public float measureSeconds = 4f;

        struct Mob
        {
            public Vector3 position;
            public int type;
            public int instanceId;   // VAT Instanced: id inside the type's renderer
            public float scale;      // edge fade, 0..1
            public Vector3 baseScale;
        }

        Quaternion _facing = Quaternion.identity;
        Matrix4x4 _facingMatrix = Matrix4x4.identity;

        Mob[] _mobs = new Mob[0];
        Transform[] _transforms = new Transform[0];
        readonly List<GameObject> _spawned = new();
        readonly List<PlayableGraph> _graphs = new();
        VATInstancedRenderer[] _instanced;
        Transform _root;

        // stats
        readonly FrameTiming[] _timing = new FrameTiming[1];
        float _accTime, _accDt;
        double _accMain, _accRender, _accGpu;
        int _accFrames;
        float _fps, _ms;
        double _mainMs, _renderMs, _gpuMs;

        // sweep
        readonly List<string> _results = new();
        bool _sweeping;
        string _sweepStatus = "";

        void Awake()
        {
            // Don't let the default 30 fps mobile cap hide differences. Still capped by vsync/display refresh,
            // so compare CPU/GPU milliseconds, not just FPS.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 120;
            if (mainLight == null) mainLight = RenderSettings.sun;
            _root = new GameObject("Mobs").transform;
        }

        void Start() => Spawn();

        void OnDestroy() => Clear();

        // ------------------------------------------------------------------ spawning

        public void Spawn()
        {
            Clear();
            if (monsters == null || monsters.Length == 0) { Debug.LogError("[VAT] No baked monsters assigned."); return; }
            _mobs = new Mob[count];
            _transforms = new Transform[count];
            _instanced = new VATInstancedRenderer[monsters.Length];
            for (int t = 0; t < monsters.Length; t++) _instanced[t] = new VATInstancedRenderer(monsters[t]);

            Vector3 dir = MoveDir;
            _facing = Quaternion.LookRotation(dir, Vector3.up); // models face +Z, so this turns them to face the run direction
            _facingMatrix = Matrix4x4.Rotate(_facing);

            var rng = new System.Random(1234); // same layout for every mode
            var layout = BuildLayout(rng);
            for (int i = 0; i < count; i++)
            {
                int type = layout[i].type;
                var data = monsters[type];
                var pos = layout[i].pos;
                float offset = (float)rng.NextDouble();
                float s = EdgeScale(pos);
                _mobs[i] = new Mob { position = pos, type = type, scale = s };

                switch (mode)
                {
                    case Mode.Skinned:
                    {
                        var go = Instantiate(data.sourcePrefab, pos, _facing * data.sourcePrefab.transform.rotation, _root);
                        _mobs[i].baseScale = go.transform.localScale;
                        go.transform.localScale = _mobs[i].baseScale * s;
                        var animator = go.GetComponent<Animator>();
                        if (animator == null) animator = go.AddComponent<Animator>();
                        animator.applyRootMotion = false;
                        var clip = data.sourceClips[data.defaultClip];
                        var playable = AnimationPlayableUtilities.PlayClip(animator, clip, out var graph);
                        playable.SetTime(offset * clip.length);
                        _graphs.Add(graph);
                        _spawned.Add(go);
                        _transforms[i] = go.transform;
                        break;
                    }
                    case Mode.VATGameObjects:
                    {
                        var go = new GameObject(data.name);
                        go.transform.SetParent(_root, false);
                        go.transform.SetPositionAndRotation(pos, _facing);
                        _mobs[i].baseScale = Vector3.one;
                        go.transform.localScale = Vector3.one * s;
                        go.AddComponent<MeshFilter>().sharedMesh = data.mesh;
                        var mr = go.AddComponent<MeshRenderer>();
                        mr.sharedMaterials = data.Materials;
                        mr.receiveShadows = false;
                        mr.lightProbeUsage = LightProbeUsage.Off;
                        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                        var anim = go.AddComponent<VATAnimator>();
                        anim.data = data;
                        anim.randomizeStart = false;
                        anim.Play(data.defaultClip, offset);
                        _spawned.Add(go);
                        _transforms[i] = go.transform;
                        break;
                    }
                    case Mode.VATInstanced:
                        _mobs[i].instanceId = _instanced[type].Add(MobMatrix(pos, s), data.defaultClip, offset);
                        break;
                }
            }
        }

        Vector3 MoveDir => moveDirection.sqrMagnitude > 0f ? new Vector3(moveDirection.x, 0f, moveDirection.z).normalized : Vector3.back;

        /// Positions for every mob. Lanes: one lane per monster type, each an evenly spaced grid with jitter.
        (int type, Vector3 pos)[] BuildLayout(System.Random rng)
        {
            var result = new (int type, Vector3 pos)[count];
            Vector3 dir = MoveDir;
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            Vector3 center = transform.position;
            float Rand() => (float)rng.NextDouble() - 0.5f;

            if (!laneFormation)
            {
                for (int i = 0; i < count; i++)
                    result[i] = (i % monsters.Length, center + right * (Rand() * area.x) + dir * (Rand() * area.y));
                return result;
            }

            int types = monsters.Length;
            float laneWidth = Mathf.Max(0.5f, (area.x - laneGap * (types - 1)) / types);
            int i0 = 0;
            for (int t = 0; t < types; t++)
            {
                int n = count / types + (t < count % types ? 1 : 0);
                if (n == 0) continue;
                // pick columns so cells are roughly square
                int cols = Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(n * laneWidth / area.y)), 1, n);
                int rows = Mathf.CeilToInt(n / (float)cols);
                float cellW = laneWidth / cols, cellL = area.y / rows;
                float laneStart = -area.x * 0.5f + t * (laneWidth + laneGap);
                for (int j = 0; j < n; j++)
                {
                    int c = j % cols, r = j / cols;
                    float x = laneStart + (c + 0.5f + Rand() * formationJitter) * cellW;
                    float z = -area.y * 0.5f + (r + 0.5f + Rand() * formationJitter) * cellL;
                    result[i0 + j] = (t, center + right * x + dir * z);
                }
                i0 += n;
            }
            return result;
        }

        /// 0 at the ends of the field, 1 inside: mobs grow in at the back and shrink out at the front.
        float EdgeScale(Vector3 pos)
        {
            if (edgeFadeDistance <= 0f) return 1f;
            float along = Vector3.Dot(pos - transform.position, MoveDir);
            float half = area.y * 0.5f;
            return Mathf.Clamp01(Mathf.Min(along + half, half - along) / edgeFadeDistance);
        }

        Matrix4x4 MobMatrix(Vector3 pos, float scale)
        {
            var m = _facingMatrix;
            if (scale != 1f)
            {
                m.m00 *= scale; m.m01 *= scale; m.m02 *= scale;
                m.m10 *= scale; m.m11 *= scale; m.m12 *= scale;
                m.m20 *= scale; m.m21 *= scale; m.m22 *= scale;
            }
            m.m03 = pos.x; m.m13 = pos.y; m.m23 = pos.z;
            return m;
        }

        public void Clear()
        {
            foreach (var g in _graphs) if (g.IsValid()) g.Destroy();
            _graphs.Clear();
            foreach (var go in _spawned) if (go) Destroy(go);
            _spawned.Clear();
            if (_instanced != null) foreach (var r in _instanced) r.Clear();
        }

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            float dt = Time.deltaTime;
            Vector3 dir = MoveDir;
            Vector3 center = transform.position;
            float half = area.y * 0.5f;

            if (move)
            {
                Vector3 step = dir * (moveSpeed * dt);
                for (int i = 0; i < _mobs.Length; i++)
                {
                    ref var m = ref _mobs[i];
                    m.position += step;
                    if (Vector3.Dot(m.position - center, dir) > half) m.position -= dir * area.y; // wrap to the back
                    float s = EdgeScale(m.position);

                    if (mode == Mode.VATInstanced)
                    {
                        _instanced[m.type].SetMatrix(m.instanceId, MobMatrix(m.position, s));
                    }
                    else
                    {
                        var t = _transforms[i];
                        t.position = m.position;
                        if (s != m.scale) t.localScale = m.baseScale * s; // only mobs near the ends pay for this
                    }
                    m.scale = s;
                }
            }

            if (mode == Mode.VATInstanced && _instanced != null)
            {
                var bounds = new Bounds(center + Vector3.up * 5f, new Vector3(area.x + 10f, 20f, area.y + 10f));
                var shadows = mainLight != null && mainLight.shadows != LightShadows.None ? ShadowCastingMode.On : ShadowCastingMode.Off;
                foreach (var r in _instanced) r.Render(bounds, shadows);
            }

            UpdateStats();
        }

        void UpdateStats()
        {
            FrameTimingManager.CaptureFrameTimings();
            uint n = FrameTimingManager.GetLatestTimings(1, _timing);

            _accDt += Time.unscaledDeltaTime;
            _accFrames++;
            if (n > 0)
            {
                _accMain += _timing[0].cpuMainThreadFrameTime;
                _accRender += _timing[0].cpuRenderThreadFrameTime;
                _accGpu += _timing[0].gpuFrameTime;
            }

            _accTime += Time.unscaledDeltaTime;
            if (_accTime < 0.5f) return;
            _ms = _accDt / _accFrames * 1000f;
            _fps = _accFrames / _accDt;
            _mainMs = _accMain / _accFrames;
            _renderMs = _accRender / _accFrames;
            _gpuMs = _accGpu / _accFrames;
            _accTime = _accDt = 0; _accMain = _accRender = _accGpu = 0; _accFrames = 0;
        }

        // ------------------------------------------------------------------ sweep

        public bool IsSweeping => _sweeping;

        public void RunSweep()
        {
            if (!_sweeping) StartCoroutine(Sweep());
        }

        IEnumerator Sweep()
        {
            _sweeping = true;
            _results.Clear();
            string header = "mode,count,fps,frame_ms,cpu_main_ms,cpu_render_ms,gpu_ms";
            _results.Add(header);
            var prevMode = mode; int prevCount = count;

            foreach (Mode m in VisibleModes)
            {
                foreach (int c in sweepCounts)
                {
                    mode = m; count = c;
                    Spawn();
                    _sweepStatus = $"{ModeNames[(int)m]} x{c}: warming up";
                    yield return new WaitForSecondsRealtime(warmupSeconds);

                    _sweepStatus = $"{ModeNames[(int)m]} x{c}: measuring";
                    float t = 0, dtSum = 0; double main = 0, render = 0, gpu = 0; int frames = 0;
                    while (t < measureSeconds)
                    {
                        yield return null;
                        t += Time.unscaledDeltaTime;
                        dtSum += Time.unscaledDeltaTime;
                        if (FrameTimingManager.GetLatestTimings(1, _timing) > 0)
                        {
                            main += _timing[0].cpuMainThreadFrameTime;
                            render += _timing[0].cpuRenderThreadFrameTime;
                            gpu += _timing[0].gpuFrameTime;
                        }
                        frames++;
                    }
                    _results.Add($"{ModeNames[(int)m]},{c},{frames / dtSum:0.0},{dtSum / frames * 1000:0.00},{main / frames:0.00},{render / frames:0.00},{gpu / frames:0.00}");
                }
            }

            mode = prevMode; count = prevCount;
            Spawn();

            string csv = string.Join("\n", _results);
            string path = Path.Combine(Application.persistentDataPath, $"vat_benchmark_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(path, csv);
            Debug.Log($"[VAT] Benchmark results ({SystemInfo.deviceModel}, {SystemInfo.graphicsDeviceType}) saved to {path}\n{csv}");
            _sweepStatus = $"Saved: {path}";
            _sweeping = false;
        }

        // ------------------------------------------------------------------ HUD (IMGUI keeps the test self-contained)

        GUIStyle _label, _centeredLabel, _button, _box;

        static Texture2D SolidTexture(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // Explicit backgrounds: the default runtime skin's button/box textures can render invisible.
        void CreateStyles()
        {
            var panel = SolidTexture(new Color(0f, 0f, 0f, 0.6f));
            var normal = SolidTexture(new Color(0.22f, 0.24f, 0.28f, 0.95f));
            var pressed = SolidTexture(new Color(0.35f, 0.38f, 0.44f, 1f));
            var selected = SolidTexture(new Color(0.16f, 0.5f, 0.9f, 1f));
            var selectedPressed = SolidTexture(new Color(0.25f, 0.6f, 1f, 1f));

            _box = new GUIStyle { padding = new RectOffset(10, 10, 10, 10) };
            _box.normal.background = panel;

            _label = new GUIStyle(GUI.skin.label) { fontSize = 16, richText = true };
            _label.normal.textColor = Color.white;
            _centeredLabel = new GUIStyle(_label) { fontSize = 18, alignment = TextAnchor.MiddleCenter };

            _button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                margin = new RectOffset(3, 3, 3, 3),
                border = new RectOffset(0, 0, 0, 0),
            };
            // off states (also hover/focused, which a desktop mouse triggers)
            foreach (var s in new[] { _button.normal, _button.hover, _button.focused })
            {
                s.background = normal;
                s.textColor = Color.white;
            }
            _button.active.background = pressed;
            _button.active.textColor = Color.white;
            // on states = selected toolbar entry / enabled toggle
            foreach (var s in new[] { _button.onNormal, _button.onHover, _button.onFocused })
            {
                s.background = selected;
                s.textColor = Color.white;
            }
            _button.onActive.background = selectedPressed;
            _button.onActive.textColor = Color.white;
        }

        void OnGUI()
        {
            float scale = Mathf.Max(1f, Mathf.Min(Screen.width, Screen.height) / 720f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            if (_button == null || _box.normal.background == null) CreateStyles();

            // While sweeping, draw one label only: the full HUD costs ~19 draw calls + CPU and would skew results.
            if (_sweeping)
            {
                GUI.Label(new Rect(10, 10, 600, 30), _sweepStatus, _label);
                return;
            }

            GUILayout.BeginArea(new Rect(10, 10, 480, Screen.height / scale - 20));
            GUILayout.BeginVertical(_box); // panel background sized to its content

            var sb = new StringBuilder();
            sb.AppendLine($"<b>{ModeNames[(int)mode]}</b>  x{count}   <b>{_fps:0} fps</b> ({_ms:0.0} ms)");
            sb.AppendLine($"CPU main {_mainMs:0.0} ms | render {_renderMs:0.0} ms | GPU {(_gpuMs > 0 ? $"{_gpuMs:0.0} ms" : "n/a")}");
            sb.Append($"<size=12>{SystemInfo.deviceModel} | {SystemInfo.graphicsDeviceType}</size>");
            GUILayout.Label(sb.ToString(), _label);

            GUI.enabled = !_sweeping;
            var modes = VisibleModes;
            int current = System.Array.IndexOf(modes, mode);
            int picked = GUILayout.Toolbar(current, System.Array.ConvertAll(modes, m => ModeNames[(int)m]), _button, GUILayout.Height(40));
            if (picked != current && picked >= 0) { mode = modes[picked]; Spawn(); }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("−", _button, GUILayout.Height(40), GUILayout.Width(60))) StepCount(-1);
            GUILayout.Label($"<b>{count}</b> mobs", _centeredLabel, GUILayout.Height(40));
            if (GUILayout.Button("+", _button, GUILayout.Height(40), GUILayout.Width(60))) StepCount(+1);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            move = GUILayout.Toggle(move, "Move", _button, GUILayout.Height(40));
            if (mainLight != null)
            {
                bool sh = mainLight.shadows != LightShadows.None;
                bool newSh = GUILayout.Toggle(sh, "Shadows", _button, GUILayout.Height(40));
                if (newSh != sh) mainLight.shadows = newSh ? LightShadows.Hard : LightShadows.None;
            }
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Run Sweep (all modes x counts)", _button, GUILayout.Height(44)))
                RunSweep();
            GUI.enabled = true;

            if (_sweepStatus.Length > 0) GUILayout.Label($"<size=13>{_sweepStatus}</size>", _label);
            if (_results.Count > 1)
                GUILayout.Label("<size=13>" + string.Join("\n", _results).Replace(",", "  ") + "</size>", _label);

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        void StepCount(int dir)
        {
            int idx = System.Array.FindIndex(CountSteps, c => c >= count);
            if (idx < 0) idx = CountSteps.Length - 1;
            idx = Mathf.Clamp(idx + dir, 0, CountSteps.Length - 1);
            count = CountSteps[idx];
            Spawn();
        }
    }
}

using UnityEngine;

namespace VAT
{
    /// Plays VAT clips on a MeshRenderer (one GameObject per mob).
    /// It only writes to the GPU when the clip changes; the per-frame animation runs in the shader.
    [ExecuteAlways]
    [RequireComponent(typeof(MeshRenderer))]
    public class VATAnimator : MonoBehaviour
    {
        public VATAnimationData data;
        [Tooltip("Clip played on enable. Empty = data.defaultClip.")]
        public string startClip;
        public float speed = 1f;
        [Tooltip("Start at a random point of the clip so crowds don't move in lockstep.")]
        public bool randomizeStart = true;

        static MaterialPropertyBlock s_Block;

        MeshRenderer _renderer;
        int _clip = -1;
        float _startTime;

        public int CurrentClip => _clip;

        /// Seconds since the current clip started (scaled by speed).
        public float ClipTime => (Time.timeSinceLevelLoad - _startTime) * speed;

        public bool IsFinished => _clip >= 0 && !data.clips[_clip].loop && ClipTime >= data.clips[_clip].Length;

        void OnEnable()
        {
            if (data == null || data.clips.Length == 0) return;
            int clip = string.IsNullOrEmpty(startClip) ? data.defaultClip : Mathf.Max(0, data.FindClip(startClip));
            Play(clip, randomizeStart && Application.isPlaying ? Random.value : 0f);
        }

        public void Play(string clipName, float normalizedOffset = 0f)
        {
            int i = data.FindClip(clipName);
            if (i < 0) { Debug.LogWarning($"VAT clip '{clipName}' not found on {data.name}", this); return; }
            Play(i, normalizedOffset);
        }

        /// Starts a clip. normalizedOffset (0..1) starts part-way through the clip.
        public void Play(int clipIndex, float normalizedOffset = 0f)
        {
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
            s_Block ??= new MaterialPropertyBlock();

            _clip = clipIndex;
            float offsetSeconds = normalizedOffset * data.clips[clipIndex].Length / Mathf.Max(speed, 0.0001f);
            _startTime = Time.timeSinceLevelLoad - offsetSeconds; // _Time.y in shaders == Time.timeSinceLevelLoad

            s_Block.SetVector(VATAnimationData.ClipId, data.GetClipParams(clipIndex));
            s_Block.SetVector(VATAnimationData.PlaybackId, VATAnimationData.GetPlaybackParams(_startTime, speed));
            _renderer.SetPropertyBlock(s_Block);
        }
    }
}

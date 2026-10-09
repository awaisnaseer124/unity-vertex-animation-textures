using System;
using UnityEngine;

namespace VAT
{
    /// One baked animation clip inside the VAT texture.
    /// Frames of all clips are stacked one after another: clip N starts at <see cref="startFrame"/>.
    [Serializable]
    public struct VATClip
    {
        public string name;
        public int startFrame;   // first baked frame of this clip in the texture
        public int frameCount;   // number of baked frames
        public float fps;        // playback rate in baked frames per second
        public bool loop;

        public float Length => loop ? frameCount / fps : Mathf.Max(0, frameCount - 1) / fps;
    }

    /// Everything the runtime needs to play a baked mesh: mesh, textures, material and the clip table.
    /// Created by the VAT Baker (Tools > VAT).
    public class VATAnimationData : ScriptableObject
    {
        public Mesh mesh;
        public Material material;      // first submesh's material (most models have just one)
        [Tooltip("One material per submesh. Models with several textures (e.g. Mixamo body + hair) get one submesh per texture.")]
        public Material[] materials = Array.Empty<Material>();
        public Texture2D positionTexture;
        public Texture2D normalTexture;
        public int vertexCount;
        public VATClip[] clips = Array.Empty<VATClip>();
        public int defaultClip;

        [Header("Source (used by the benchmark's Skinned mode)")]
        public GameObject sourcePrefab;
        public AnimationClip[] sourceClips = Array.Empty<AnimationClip>();

        // Shader property IDs shared by everything that drives VAT materials.
        public static readonly int ClipId = Shader.PropertyToID("_VATClip");
        public static readonly int PlaybackId = Shader.PropertyToID("_VATPlayback");

        /// Materials indexed by submesh (falls back to the single material for older bakes).
        public Material[] Materials => materials != null && materials.Length > 0 ? materials : new[] { material };

        public int FindClip(string clipName)
        {
            for (int i = 0; i < clips.Length; i++)
                if (string.Equals(clips[i].name, clipName, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        /// x = start frame, y = frame count, z = fps, w = loop (1/0). Matches _VATClip in VATCommon.hlsl.
        public Vector4 GetClipParams(int clipIndex)
        {
            var c = clips[clipIndex];
            return new Vector4(c.startFrame, c.frameCount, c.fps, c.loop ? 1f : 0f);
        }

        /// x = start time (in shader _Time.y units = Time.timeSinceLevelLoad), y = speed. Matches _VATPlayback.
        public static Vector4 GetPlaybackParams(float startTime, float speed) => new Vector4(startTime, speed, 0f, 0f);
    }
}

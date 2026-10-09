using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VAT
{
    /// Draws many VAT mobs of ONE type with no GameObjects at all, using Graphics.RenderMeshInstanced.
    /// Each batch is up to 1023 instances = one draw call (per pass). The clip arrays are uploaded only
    /// when a clip changes; per frame the caller just writes matrices.
    ///
    /// Note: there is no per-instance frustum culling here. Each batch is culled only by the worldBounds
    /// passed to Render(), so for big levels you would bucket mobs spatially.
    public class VATInstancedRenderer
    {
        public const int MaxPerBatch = 1023;

        class Batch
        {
            public readonly Matrix4x4[] matrices = new Matrix4x4[MaxPerBatch];
            public readonly Vector4[] clip = new Vector4[MaxPerBatch];
            public readonly Vector4[] playback = new Vector4[MaxPerBatch];
            public readonly MaterialPropertyBlock block = new();
            public int count;
            public bool dirty = true;
        }

        readonly VATAnimationData _data;
        readonly Material[] _materials;
        readonly List<Batch> _batches = new();

        public int Count { get; private set; }
        public VATAnimationData Data => _data;

        public VATInstancedRenderer(VATAnimationData data)
        {
            _data = data;
            _materials = data.Materials;
        }

        /// Adds an instance and returns its id.
        public int Add(Matrix4x4 matrix, int clipIndex, float normalizedOffset = 0f, float speed = 1f)
        {
            int id = Count++;
            if (id / MaxPerBatch >= _batches.Count) _batches.Add(new Batch());
            var b = _batches[id / MaxPerBatch];
            b.count++;
            b.matrices[id % MaxPerBatch] = matrix;
            Play(id, clipIndex, normalizedOffset, speed);
            return id;
        }

        public void Play(int id, int clipIndex, float normalizedOffset = 0f, float speed = 1f)
        {
            var b = _batches[id / MaxPerBatch];
            int i = id % MaxPerBatch;
            float start = Time.timeSinceLevelLoad - normalizedOffset * _data.clips[clipIndex].Length / Mathf.Max(speed, 0.0001f);
            b.clip[i] = _data.GetClipParams(clipIndex);
            b.playback[i] = VATAnimationData.GetPlaybackParams(start, speed);
            b.dirty = true;
        }

        public void SetMatrix(int id, in Matrix4x4 matrix) => _batches[id / MaxPerBatch].matrices[id % MaxPerBatch] = matrix;

        public void Clear()
        {
            _batches.Clear();
            Count = 0;
        }

        public void Render(Bounds worldBounds, ShadowCastingMode shadows = ShadowCastingMode.On, int layer = 0)
        {
            foreach (var b in _batches)
            {
                if (b.count == 0) continue;
                if (b.dirty)
                {
                    b.block.SetVectorArray(VATAnimationData.ClipId, b.clip);
                    b.block.SetVectorArray(VATAnimationData.PlaybackId, b.playback);
                    b.dirty = false;
                }
                // One instanced draw per submesh (usually just one).
                for (int s = 0; s < _materials.Length; s++)
                {
                    var rp = new RenderParams(_materials[s])
                    {
                        matProps = b.block,
                        worldBounds = worldBounds,
                        shadowCastingMode = shadows,
                        receiveShadows = false,
                        layer = layer,
                    };
                    Graphics.RenderMeshInstanced(rp, _data.mesh, s, b.matrices, b.count);
                }
            }
        }
    }
}

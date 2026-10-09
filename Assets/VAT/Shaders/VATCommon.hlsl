#ifndef VAT_COMMON_INCLUDED
#define VAT_COMMON_INCLUDED

// ---------------------------------------------------------------------------------------------
// Vertex Animation Texture sampling.
//
// Texture layout (written by VATBaker.cs):
//   Every baked frame stores one texel per vertex. Texels are laid out linearly:
//       texelIndex = (clipStartFrame + frame) * vertexCount + vertexId
//   and wrapped into a 2D texture of width _VATTexSize.x:
//       x = texelIndex % width,  y = texelIndex / width
//   Position texture: RGB = object-space position.  Normal texture: RGB = object-space normal.
//
// Per-instance data (set from C# via MaterialPropertyBlock or instanced arrays):
//   _VATClip     = (startFrame, frameCount, fps, loop)
//   _VATPlayback = (startTime, speed, 0, 0)       startTime is compared against _Time.y
//
// The current frame is computed entirely on the GPU from _Time, so the CPU does nothing per frame.
// ---------------------------------------------------------------------------------------------

TEXTURE2D(_VATPositionTex);
TEXTURE2D(_VATNormalTex);

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4  _BaseColor;
    float4 _VATTexSize;      // x = width, y = height, z = vertexCount, w = unused
    half   _VATInterpolate;  // 1 = blend between baked frames, 0 = snap (cheaper, choppier)
CBUFFER_END

UNITY_INSTANCING_BUFFER_START(VATProps)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATClip)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATPlayback)
UNITY_INSTANCING_BUFFER_END(VATProps)

uint2 VATTexelCoord(uint texelIndex)
{
    uint width = (uint)_VATTexSize.x;
    return uint2(texelIndex % width, texelIndex / width);
}

// Returns the animated object-space position/normal of one vertex.
void VATSample(float vertexId, out float3 positionOS, out float3 normalOS)
{
    float4 clip     = UNITY_ACCESS_INSTANCED_PROP(VATProps, _VATClip);
    float4 playback = UNITY_ACCESS_INSTANCED_PROP(VATProps, _VATPlayback);

    float frameCount = max(clip.y, 1.0);
    bool  loop       = clip.w > 0.5;

    // Time -> fractional frame.
    float t = max(0.0, (_Time.y - playback.x) * playback.y * clip.z);
    float frame = loop ? fmod(t, frameCount) : min(t, frameCount - 1.0);

    float f0 = floor(frame);
    float f1 = f0 + 1.0;
    f1 = loop ? (f1 >= frameCount ? 0.0 : f1) : min(f1, frameCount - 1.0);
    float blend = (frame - f0) * _VATInterpolate;

    uint vertexCount = (uint)_VATTexSize.z;
    uint vid   = (uint)(vertexId + 0.5);
    uint base  = (uint)clip.x;
    uint i0 = (base + (uint)f0) * vertexCount + vid;
    uint i1 = (base + (uint)f1) * vertexCount + vid;

    // Point "Load" (texelFetch) - no filtering, exact texel, works in the vertex stage on GLES3/Vulkan/Metal.
    float3 p0 = LOAD_TEXTURE2D_LOD(_VATPositionTex, VATTexelCoord(i0), 0).xyz;
    float3 p1 = LOAD_TEXTURE2D_LOD(_VATPositionTex, VATTexelCoord(i1), 0).xyz;
    float3 n0 = LOAD_TEXTURE2D_LOD(_VATNormalTex,   VATTexelCoord(i0), 0).xyz;
    float3 n1 = LOAD_TEXTURE2D_LOD(_VATNormalTex,   VATTexelCoord(i1), 0).xyz;

    positionOS = lerp(p0, p1, blend);
    normalOS   = normalize(lerp(n0, n1, blend));
}

#endif

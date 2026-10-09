// Simple mobile-friendly lit shader that plays Vertex Animation Textures.
// Lighting: main light Lambert + ambient SH + fog. Casts shadows (with the animated pose).
Shader "VAT/Simple Lit"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)

        [Header(VAT)]
        [NoScaleOffset] _VATPositionTex ("Position Texture", 2D) = "black" {}
        [NoScaleOffset] _VATNormalTex ("Normal Texture", 2D) = "black" {}
        _VATTexSize ("Tex Size (w, h, vertexCount, -)", Vector) = (1,1,1,0)
        [ToggleUI] _VATInterpolate ("Interpolate Frames", Float) = 1

        // Per-instance values. Material values act as defaults (first clip, looping) so the
        // baked prefab animates even with no script attached.
        _VATClip ("Clip (start, count, fps, loop)", Vector) = (0,1,30,1)
        _VATPlayback ("Playback (startTime, speed)", Vector) = (0,1,0,0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "VATCommon.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;   // bind pose, unused for drawing (kept for bounds/fallback)
                float2 uv         : TEXCOORD0;
                float2 vatId      : TEXCOORD2;  // x = vertex index into the VAT texture
                half4  color      : COLOR;      // baked material color
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                half4  color      : COLOR;
                half   fogFactor  : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);

                float3 posOS, nrmOS;
                VATSample(v.vatId.x, posOS, nrmOS);

                float3 posWS = TransformObjectToWorld(posOS);
                o.positionCS = TransformWorldToHClip(posWS);
                o.normalWS   = TransformObjectToWorldNormal(nrmOS);
                o.uv         = TRANSFORM_TEX(v.uv, _BaseMap);
                o.color      = v.color * _BaseColor;
                o.fogFactor  = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * i.color;
                half3 n = normalize(i.normalWS);

                Light light = GetMainLight();
                half3 diffuse = light.color * saturate(dot(n, light.direction));
                half3 ambient = SampleSH(n);

                half3 col = albedo.rgb * (diffuse + ambient);
                col = MixFog(col, i.fogFactor);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 vatId      : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 vert(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                float3 posOS, nrmOS;
                VATSample(v.vatId.x, posOS, nrmOS);

                float3 posWS = TransformObjectToWorld(posOS);
                float3 nrmWS = TransformObjectToWorldNormal(nrmOS);
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, nrmWS, _LightDirection));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        // Needed when the renderer does a depth+normals prepass (SSAO, some post effects).
        // Without it VAT mobs would be missing from SSAO and skip work Lit meshes do.
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 vatId      : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3  normalWS   : TEXCOORD0;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                float3 posOS, nrmOS;
                VATSample(v.vatId.x, posOS, nrmOS);
                o.positionCS = TransformObjectToHClip(posOS);
                o.normalWS = TransformObjectToWorldNormal(nrmOS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 oct = saturate(PackNormalOctQuadEncode(n) * 0.5 + 0.5);
                return half4(PackFloat2To888(oct), 0.0);
            #else
                return half4(n, 0.0);
            #endif
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 vatId      : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 vert(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                float3 posOS, nrmOS;
                VATSample(v.vatId.x, posOS, nrmOS);
                return TransformObjectToHClip(posOS);
            }

            half frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}

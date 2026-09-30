// Stone grain laid over the White House walls, in world metres.
//
// The building's textures are 4096 px atlases over a 150 m building, 7 to 13 texels per metre, which is soft
// the moment a camera comes within a few metres of a wall. The glTF material it imports with has no detail
// slot, and swapping it for URP Lit would throw away the model's own roughness and occlusion packing. So
// this is a second pass on the same renderer (BuildingLodBuilder appends it as an extra material slot on the
// full-detail shells), multiplying a tileable grain onto what is already drawn:
//
//   - Blend DstColor SrcColor is a 2x multiply: 0.5 in the texture leaves the wall exactly as it was.
//   - Sampled triplanar in world space, so the grain is the same size on every wall whatever the UVs do.
//   - Fades to neutral with distance and in fog, so a wide shot is untouched.
//
// Only the LOD0 shells carry it, which the mobile tier never draws: it costs a phone nothing.
Shader "PoDecath/StoneDetail"
{
    Properties
    {
        _DetailMap ("Grain (linear, 0.5 = neutral)", 2D) = "grey" {}
        _TileMetres ("Tile size (m)", Float) = 0.8
        _Strength ("Strength", Range(0, 1)) = 1
        _FadeStart ("Fade start (m)", Float) = 12
        _FadeEnd ("Fade end (m)", Float) = 45
    }
    SubShader
    {
        // After every opaque surface in the opaque pass, so the wall is already there to multiply.
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+100" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "StoneDetail"
            Tags { "LightMode" = "UniversalForward" }
            Blend DstColor SrcColor
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_DetailMap);
            SAMPLER(sampler_DetailMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _DetailMap_ST;
                float _TileMetres;
                half _Strength;
                float _FadeStart;
                float _FadeEnd;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float fogFactor : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 p = i.positionWS / max(0.05, _TileMetres);
                float3 w = pow(abs(normalize(i.normalWS)), 4.0);
                w /= max(1e-4, w.x + w.y + w.z);
                half gx = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, p.zy).r;
                half gy = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, p.xz).r;
                half gz = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, p.xy).r;
                half grain = gx * w.x + gy * w.y + gz * w.z;

                float d = distance(i.positionWS, GetCameraPositionWS());
                half near = 1.0 - saturate((d - _FadeStart) / max(0.01, _FadeEnd - _FadeStart));
                // Fog: a surface that is mostly fog colour has no grain to show.
                half visible = near * _Strength;
                #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
                    visible *= saturate(ComputeFogIntensity(i.fogFactor));
                #endif
                half g = lerp(0.5h, grain, visible);
                return half4(g, g, g, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}

// A fresnel rim on the athlete the gallery has cut to.
//
// Drawn by a second SkinnedMeshRenderer that shares the featured athlete's mesh and bones, so the
// model's own materials and textures are never touched (house rule: athletes keep their imported look).
// Additive, so it reads as light on the silhouette rather than a tint on the skin.
Shader "PoDecath/AthleteRim"
{
    Properties
    {
        _RimColor ("Rim colour", Color) = (0.5, 0.9, 1, 1)
        _RimPower ("Rim power", Range(0.5, 8)) = 2.4
        _RimIntensity ("Intensity", Range(0, 4)) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+5" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Rim"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _RimColor;
                half _RimPower;
                half _RimIntensity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewWS : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 pw = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(pw);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.viewWS = GetWorldSpaceNormalizeViewDir(pw);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half f = 1 - saturate(dot(normalize(i.normalWS), normalize(i.viewWS)));
                f = pow(f, _RimPower);
                return half4(_RimColor.rgb * f * _RimIntensity, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}

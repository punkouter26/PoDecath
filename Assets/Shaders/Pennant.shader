// Bunting along the rail. One mesh of triangles; each pennant's tip (uv.y = 1) flaps along its normal
// with the global wind SceneLook publishes, and the rope (uv.y = 0) stays put. Lit by the main light and
// the sky probe, two-sided.
Shader "PoDecath/Pennant"
{
    Properties
    {
        _Amplitude ("Flap (m)", Float) = 0.14
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Amplitude;
            CBUFFER_END

            float _PoDecathWind;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 seed : TEXCOORD1;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float fog : TEXCOORD1;
                float3 posWS : TEXCOORD2;
                float4 color : COLOR;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 pw = TransformObjectToWorld(v.positionOS.xyz);
                float3 n = TransformObjectToWorldNormal(v.normalOS);
                float t = _Time.y;
                float w = saturate(_PoDecathWind);
                float ph = v.seed.x * 6.2831853;
                float flap = sin(t * (5.0 + 3.0 * w) + ph + v.uv.y * 2.5) * _Amplitude * (0.15 + w) * v.uv.y * v.uv.y;
                float lift = (sin(t * 2.2 + ph) * 0.5 + 0.5) * 0.06 * w * v.uv.y;
                pw += n * flap + float3(0, lift, 0);
                o.positionCS = TransformWorldToHClip(pw);
                o.normalWS = n;
                o.posWS = pw;
                o.color = v.color;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                float3 n = normalize(i.normalWS) * IS_FRONT_VFACE(face, 1.0, -1.0);
                Light l = GetMainLight(TransformWorldToShadowCoord(i.posWS));
                half ndl = saturate(dot(n, l.direction)) * 0.75 + 0.25;
                half3 col = i.color.rgb * (l.color * ndl * l.shadowAttenuation + SampleSH(n) * 0.6);
                return half4(MixFog(col, i.fog), 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}

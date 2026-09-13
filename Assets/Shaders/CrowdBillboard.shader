// A ring of spectators drawn as one mesh of camera-facing quads.
//
// Every quad carries its pivot in POSITION, its corner (-0.5..0.5, 0..1) in TEXCOORD1, a random phase
// and energy in TEXCOORD2, and its shirt colour in COLOR. The vertex shader turns the quad to face the
// camera about the vertical, then adds three motions read off the mix: an idle bob, a bounce whose height
// and rate ride _Mood (the crowd level RaceAudio computes), and a jump on _Burst (a cheer, a groan).
// One draw call for the whole crowd, however many rows the tier can afford.
Shader "PoDecath/CrowdBillboard"
{
    Properties
    {
        _BaseMap ("Sprite atlas", 2D) = "white" {}
        _Width ("Figure width (m)", Float) = 0.9
        _Height ("Figure height (m)", Float) = 1.85
        _Mood ("Mood 0..1", Range(0, 1)) = 0.2
        _Burst ("Reaction 0..1", Range(0, 1)) = 0
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.3
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _Width;
                float _Height;
                float _Mood;
                float _Burst;
                float _Cutoff;
            CBUFFER_END

            // Set globally by SceneLook: white by day, a dim blue at night.
            float4 _PoDecathCrowdTint;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float2 corner : TEXCOORD1;
                float2 seed : TEXCOORD2;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float fog : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 pivot = TransformObjectToWorld(v.positionOS.xyz);

                // Cylindrical billboard: turn towards the camera about the vertical only, so the crowd is
                // still standing up when the stadium wide looks down on it.
                float3 toCam = _WorldSpaceCameraPos - pivot;
                toCam.y = 0;
                float3 fwd = normalize(toCam + float3(0, 0, 1e-4));
                float3 right = normalize(cross(float3(0, 1, 0), fwd));

                float phase = v.seed.x * 6.2831853;
                float energy = 0.4 + 0.6 * v.seed.y;
                float t = _Time.y;
                float bob = sin(t * 1.7 + phase) * 0.02 * (0.3 + _Mood);
                float bounce = max(0, sin(t * (4.0 + 2.0 * _Mood) + phase)) * 0.12 * _Mood * energy;
                float jump = max(0, sin(t * 9.0 + phase)) * 0.28 * _Burst * energy;
                float lean = sin(t * 1.3 + phase * 1.7) * 0.06 * (0.2 + _Mood);

                float cy = v.corner.y;
                float3 pos = pivot
                           + right * (v.corner.x * _Width + lean * cy)
                           + float3(0, 1, 0) * (cy * _Height + bob + bounce + jump);

                o.positionCS = TransformWorldToHClip(pos);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.color = v.color;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                clip(tex.a - _Cutoff);
                // Alpha is a mask as well as coverage: 1 is the shirt, which takes the vertex colour;
                // 0.6 is skin and trousers, drawn as baked into the sprite.
                half shirt = saturate((tex.a - 0.6) * 2.5);
                half3 tint = _PoDecathCrowdTint.a > 0 ? _PoDecathCrowdTint.rgb : half3(1, 1, 1);
                half3 col = tex.rgb * lerp(half3(1, 1, 1), i.color.rgb, shirt) * tint;
                col = MixFog(col, i.fog);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}

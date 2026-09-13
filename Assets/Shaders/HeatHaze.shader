// Heat shimmer over the asphalt: a transparent curtain that re-samples the opaque picture behind it
// through a slowly rolling offset. Strongest at its base and gone at the top and sides, so it never has
// a visible edge. Needs the opaque texture, which only the PC render pipeline asset requests; the
// component that places these disables itself on the mobile tier.
Shader "PoDecath/HeatHaze"
{
    Properties
    {
        _Strength ("Distortion", Range(0, 0.03)) = 0.0045
        _Speed ("Speed", Float) = 2.6
        _Scale ("Scale", Float) = 9
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Haze"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Strength;
                float _Speed;
                float _Scale;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 screen : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                o.screen = ComputeScreenPos(o.positionCS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 suv = i.screen.xy / i.screen.w;
                float t = _Time.y * _Speed;
                float n = sin(i.uv.y * _Scale * 3.1 + t + sin(i.uv.x * _Scale * 2.3 + t * 0.7) * 1.5) * 0.5
                        + sin(i.uv.x * _Scale * 5.7 - t * 1.3 + i.uv.y * 4.0) * 0.5;
                float fade = (1 - i.uv.y) * (1 - i.uv.y) * saturate(i.uv.x * 6) * saturate((1 - i.uv.x) * 6);
                float2 off = float2(n * 0.35, n) * _Strength * fade;
                half3 col = SampleSceneColor(suv + off);
                return half4(col, fade);
            }
            ENDHLSL
        }
    }
    Fallback Off
}

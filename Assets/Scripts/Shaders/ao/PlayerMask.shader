// Drawn as an OVERRIDE material over Layer "Player" renderers only (see
// PlayerAOFeature) -- writes 1 into an R8 mask texture wherever a visible
// (depth-tested against the real scene depth) Player pixel is, so the
// compositing pass knows which screen pixels should get custom Player AO
// instead of URP's built-in SSAO.
Shader "Hidden/PostProcessing/PlayerMask"
{
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "PlayerMask"
            ZTest LEqual
            ZWrite Off
            Cull Off
            ColorMask R

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                return half4(1.0h, 0.0h, 0.0h, 0.0h);
            }
            ENDHLSL
        }
    }
}

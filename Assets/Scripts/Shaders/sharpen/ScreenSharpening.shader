Shader "Hidden/PostProcessing/ScreenSharpening"
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
            Name "ScreenSharpening"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _Intensity;

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float2 texelSize = _BlitTexture_TexelSize.xy;

                // Center pixel
                float3 screen = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv).rgb;

                // 4 cardinal neighbor samples (N, S, E, W)
                float3 N = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(0, texelSize.y)).rgb;
                float3 S = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv - float2(0, texelSize.y)).rgb;
                float3 E = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(texelSize.x, 0)).rgb;
                float3 W = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv - float2(texelSize.x, 0)).rgb;

                // Blur: center 30% + neighbors average 70%
                float3 combineSample = ((N + S + E + W) / 4.0) * 0.7;
                float3 blurScreen = saturate(screen * 0.3 + combineSample);

                // Negative lerp = sharpen, so we negate intensity
                float3 result = lerp(screen, blurScreen, -_Intensity);

                return float4(saturate(result), 1.0);
            }
            ENDHLSL
        }
    }
}

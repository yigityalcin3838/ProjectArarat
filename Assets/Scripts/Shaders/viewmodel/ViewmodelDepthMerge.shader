Shader "Hidden/PostProcessing/ViewmodelDepthMerge"
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
            Name "ViewmodelDepthMerge"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Vert, Varyings and _BlitTexture come from here. The pass is driven by
            // Blitter.BlitTexture rather than a hand-rolled fullscreen triangle, so the
            // source arrives as _BlitTexture -- not _MainTex, which is what this used
            // before render graph took over the blitting.
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_WeaponDepthTex);
            SAMPLER(sampler_WeaponDepthTex);


            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                // _BlitTexture = cameraDepthTexture (sahne depth)
                float sceneDepth = SAMPLE_TEXTURE2D(_BlitTexture, sampler_PointClamp, uv).r;

                // _WeaponDepthTex = activeDepthTexture — overlay pass depth'i
                // temizleyip sadece silahi cizdigi icin, silah disinda her yer
                // cleared durumda
                float weaponDepth = SAMPLE_TEXTURE2D(_WeaponDepthTex, sampler_WeaponDepthTex, uv).r;

                // Which value "cleared" is depends on the depth convention, and
                // getting it backwards is not a subtle failure.
                //
                // On reversed-Z -- which is every desktop target Unity ships today --
                // near is 1 and far is 0, so an untouched pixel clears to 0, not 1.
                // Testing for < 1.0 there is true almost everywhere, so the whole
                // screen was taken as weapon and _CameraDepthTexture came out reading
                // 0 -- the far plane -- for the entire frame. Depth of field then has
                // a scene with no depth in it at all and blurs nothing, which is
                // exactly the symptom: the effect appears to be switched off.
                //
                // The keyword is defined by the URP core includes, so the branch is
                // resolved at compile time and costs nothing.
            #if UNITY_REVERSED_Z
                bool isWeaponPixel = weaponDepth > 0.0;
            #else
                bool isWeaponPixel = weaponDepth < 1.0;
            #endif

                return isWeaponPixel ? weaponDepth : sceneDepth;
            }
            ENDHLSL
        }
    }
}

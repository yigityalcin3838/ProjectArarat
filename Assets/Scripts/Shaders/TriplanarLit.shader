Shader "Custom/TriplanarLit"
{
    Properties
    {
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}

        _Tiling("Triplanar Tiling", Float) = 1
        _BlendSharpness("Triplanar Blend Sharpness", Range(1, 32)) = 4

        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1

        _Metallic("Metallic", Range(0,1)) = 0
        _MetallicGlossMap("Metallic (R) Smoothness (A)", 2D) = "white" {}
        _Smoothness("Smoothness", Range(0,1)) = 0.5

        _OcclusionMap("Occlusion", 2D) = "white" {}
        _OcclusionStrength("Occlusion Strength", Range(0,1)) = 1

        [HDR] _EmissionColor("Emission Color", Color) = (0,0,0,1)
        _EmissionMap("Emission Map", 2D) = "white" {}

        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull Mode", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry"
        }
        LOD 300
        Cull [_Cull]

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex TriplanarVertex
            #pragma fragment TriplanarFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS

            // See EnvironmentPBR.shader for what these two do. Same renderer,
            // same omission, same silent result: no point or spot lights, and
            // no SSAO on anything drawn with this shader.
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                half   fogFactor  : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_BaseMap);          SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);          SAMPLER(sampler_BumpMap);
            TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
            TEXTURE2D(_OcclusionMap);     SAMPLER(sampler_OcclusionMap);
            TEXTURE2D(_EmissionMap);      SAMPLER(sampler_EmissionMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _Tiling;
                float _BlendSharpness;
                float _BumpScale;
                half  _Metallic;
                half  _Smoothness;
                half  _OcclusionStrength;
                half4 _EmissionColor;
            CBUFFER_END

            Varyings TriplanarVertex(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);

                OUT.positionCS = positionInputs.positionCS;
                OUT.positionWS = positionInputs.positionWS;
                OUT.normalWS = normalWS;
                OUT.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                return OUT;
            }

            float3 GetTriplanarWeights(float3 normalWS)
            {
                float3 blend = pow(abs(normalWS), _BlendSharpness);
                return blend / max(dot(blend, float3(1, 1, 1)), 1e-5);
            }

            half4 SampleTriplanar(TEXTURE2D_PARAM(tex, samp), float3 positionWS, float3 blendWeights, float tiling)
            {
                half4 colX = SAMPLE_TEXTURE2D(tex, samp, positionWS.zy * tiling);
                half4 colY = SAMPLE_TEXTURE2D(tex, samp, positionWS.xz * tiling);
                half4 colZ = SAMPLE_TEXTURE2D(tex, samp, positionWS.xy * tiling);
                return colX * blendWeights.x + colY * blendWeights.y + colZ * blendWeights.z;
            }

            // Whiteout-blend triplanar normal mapping: each projected tangent-space
            // normal is combined with the base world normal's other two axes, then
            // swizzled back into world space and blended by the same weights.
            half3 SampleTriplanarNormalWS(float3 positionWS, float3 normalWS, float3 blendWeights, float tiling, float scale)
            {
                half3 tnormalX = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, positionWS.zy * tiling), scale);
                half3 tnormalY = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, positionWS.xz * tiling), scale);
                half3 tnormalZ = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, positionWS.xy * tiling), scale);

                tnormalX = half3(tnormalX.xy + normalWS.zy, normalWS.x);
                tnormalY = half3(tnormalY.xy + normalWS.xz, normalWS.y);
                tnormalZ = half3(tnormalZ.xy + normalWS.xy, normalWS.z);

                return normalize(
                    tnormalX.zyx * blendWeights.x +
                    tnormalY.xzy * blendWeights.y +
                    tnormalZ.xyz * blendWeights.z);
            }

            half4 TriplanarFragment(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                float3 blendWeights = GetTriplanarWeights(IN.normalWS);

                half4 albedo = SampleTriplanar(TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap), IN.positionWS, blendWeights, _Tiling) * _BaseColor;
                half4 metallicGloss = SampleTriplanar(TEXTURE2D_ARGS(_MetallicGlossMap, sampler_MetallicGlossMap), IN.positionWS, blendWeights, _Tiling);
                half occlusionSample = SampleTriplanar(TEXTURE2D_ARGS(_OcclusionMap, sampler_OcclusionMap), IN.positionWS, blendWeights, _Tiling).g;
                half3 emission = SampleTriplanar(TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap), IN.positionWS, blendWeights, _Tiling).rgb * _EmissionColor.rgb;

                half3 normalWS = SampleTriplanarNormalWS(IN.positionWS, IN.normalWS, blendWeights, _Tiling, _BumpScale);

                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.positionCS = IN.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord = IN.fogFactor;
                inputData.vertexLighting = half3(0, 0, 0);
                inputData.bakedGI = EvaluateAmbientProbeSRGB(IN.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                half metallic = metallicGloss.r * _Metallic;
                half smoothness = metallicGloss.a * _Smoothness;
                half occlusion = lerp(1.0h, occlusionSample, _OcclusionStrength);

                half4 color = UniversalFragmentPBR(inputData, albedo.rgb, metallic, half3(0, 0, 0),
                    smoothness, occlusion, emission, albedo.a);

                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // See the identical pass in EnvironmentPBR.shader for why this is
        // required and not optional: with SSAO's Source set to "Depth
        // Normals", URP's DepthNormals prepass is what fills
        // _CameraDepthTexture, so a shader without this pass writes no
        // depth at all.
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/Shaders/UnlitDepthNormalsPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}

// URP port of "Legacy Shaders/2Bumped Specular" (Background_mountains.shader).
// Same property names as the original so existing materials keep their
// texture/color assignments when their Shader is switched to this one.
Shader "Custom/URP/BackgroundMountains"
{
    Properties
    {
        _Color ("Main Color", Color) = (1,1,1,1)
        _SpecColor ("Specular Color", Color) = (0.5, 0.5, 0.5, 1)
        _Shininess ("Shininess", Range (0.03, 1)) = 0.078125
        _Mask("Mask (RGBA)", 2D) = "red" {}
        _Splat3("Snow (a)", 2D) = "black" {}
        _Splat2("Stone (B)", 2D) = "black" {}
        _Splat1("Cliff (G)", 2D) = "black" {}
        _Splat0("Grass (R)", 2D) = "white" {}

        _Normal3("SnowN (A)", 2D) = "bump" {}
        _Normal2("StonesN (B)", 2D) = "bump" {}
        _Normal1("CliffN (G)", 2D) = "bump" {}
        _Normal0("GrassN (R)", 2D) = "bump" {}

        _MainTex ("MainTex", 2D) = "white" {}
        _BumpMap ("Normalmap", 2D) = "bump" {}

        _Blur("Blur", Range(0.01, 1)) = 0.02
        _HeightSplatAll("Grass(R) Cliff(G) Stones(B) Snow(a)", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        LOD 400

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            float4 _SpecColor;
            float _Shininess;
            float _Blur;
            float4 _MainTex_ST;
            float4 _Splat0_ST;
            float4 _Splat1_ST;
            float4 _Splat2_ST;
            float4 _Splat3_ST;
            float4 _BumpMap_ST;
        CBUFFER_END

        TEXTURE2D(_Mask); SAMPLER(sampler_Mask);
        TEXTURE2D(_Splat0); SAMPLER(sampler_Splat0);
        TEXTURE2D(_Splat1); SAMPLER(sampler_Splat1);
        TEXTURE2D(_Splat2); SAMPLER(sampler_Splat2);
        TEXTURE2D(_Splat3); SAMPLER(sampler_Splat3);
        TEXTURE2D(_Normal0); SAMPLER(sampler_Normal0);
        TEXTURE2D(_Normal1); SAMPLER(sampler_Normal1);
        TEXTURE2D(_Normal2); SAMPLER(sampler_Normal2);
        TEXTURE2D(_Normal3); SAMPLER(sampler_Normal3);
        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        TEXTURE2D(_HeightSplatAll); SAMPLER(sampler_HeightSplatAll);
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float4 tangentWS : TEXCOORD3;
                float fogFactor : TEXCOORD4;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionHCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS = normInputs.normalWS;
                OUT.tangentWS = float4(normInputs.tangentWS, IN.tangentOS.w);
                OUT.uv = IN.uv;
                OUT.fogFactor = ComputeFogFactor(posInputs.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uvMain = TRANSFORM_TEX(IN.uv, _MainTex);
                float2 uv0 = TRANSFORM_TEX(IN.uv, _Splat0);
                float2 uv1 = TRANSFORM_TEX(IN.uv, _Splat1);
                float2 uv2 = TRANSFORM_TEX(IN.uv, _Splat2);
                float2 uv3 = TRANSFORM_TEX(IN.uv, _Splat3);
                float2 uvBump = TRANSFORM_TEX(IN.uv, _BumpMap);

                half4 ColorTex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvMain);
                half4 MaskTex = SAMPLE_TEXTURE2D(_Mask, sampler_Mask, uvMain);

                half4 Detail0 = SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, uv0) * ColorTex * _Color;
                half4 Detail1 = SAMPLE_TEXTURE2D(_Splat1, sampler_Splat1, uv1) * ColorTex * _Color;
                half4 Detail2 = SAMPLE_TEXTURE2D(_Splat2, sampler_Splat2, uv2) * _Color;
                half4 Detail3 = SAMPLE_TEXTURE2D(_Splat3, sampler_Splat3, uv3);

                half HeightSplatTex1 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uv0).r;
                half HeightSplatTex2 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uv1).g;
                half HeightSplatTex3 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uv2).b;
                half HeightSplatTex4 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uv3).a;

                float a0 = MaskTex.r;
                float a1 = MaskTex.g;
                float a2 = MaskTex.b;
                float a3 = MaskTex.a;

                float ma = max(max(max(HeightSplatTex1 + a0, HeightSplatTex2 + a1), HeightSplatTex3 + a2), HeightSplatTex4 + a3) - _Blur;

                float b0 = max(HeightSplatTex1 + a0 - ma, 0);
                float b1 = max(HeightSplatTex2 + a1 - ma, 0);
                float b2 = max(HeightSplatTex3 + a2 - ma, 0);
                float b3 = max(HeightSplatTex4 + a3 - ma, 0);
                float bSum = max(b0 + b1 + b2 + b3, 1e-5);

                half4 tex = (Detail0 * b0 + Detail1 * b1 + Detail2 * b2 + Detail3 * b3) / bSum;

                half4 n0 = SAMPLE_TEXTURE2D(_Normal0, sampler_Normal0, uv0);
                half4 n1 = SAMPLE_TEXTURE2D(_Normal1, sampler_Normal1, uv1);
                half4 n2 = SAMPLE_TEXTURE2D(_Normal2, sampler_Normal2, uv2);
                half4 n3 = SAMPLE_TEXTURE2D(_Normal3, sampler_Normal3, uv3);
                half4 mixnormal = (n0 * b0 + n1 * b1 + n2 * b2 + n3 * b3) / bSum;

                half3 bumpMapN = UnpackNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvBump));
                half3 tangentNormal = normalize(UnpackNormal(mixnormal) * 1.5 + bumpMapN * 3);

                float3 normalWS = normalize(IN.normalWS);
                float3 tangentWS = normalize(IN.tangentWS.xyz);
                float3 bitangentWS = cross(normalWS, tangentWS) * IN.tangentWS.w;
                float3x3 tangentToWorld = float3x3(tangentWS, bitangentWS, normalWS);
                half3 worldNormal = normalize(mul(tangentNormal, tangentToWorld));

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(IN.positionWS));
                half3 viewDirWS = normalize(GetWorldSpaceViewDir(IN.positionWS));
                half3 halfDir = normalize(mainLight.direction + viewDirWS);

                half NdotL = saturate(dot(worldNormal, mainLight.direction));
                half NdotH = saturate(dot(worldNormal, halfDir));

                half3 albedo = tex.rgb;
                half gloss = tex.a;
                // Matches the built-in BlinnPhong lighting model exactly:
                // spec = pow(NdotH, Specular*128) * Gloss, NOT scaled by the
                // NdotL VALUE, and the exponent is a plain linear 0-128 scale
                // (not a PBR roughness curve). It IS gated off (not just
                // scaled) when the surface faces away from the light --
                // half-vector alignment alone can still be high there, which
                // without this shows up as specular "leaking" through onto
                // the dark side of geometry.
                half specTerm = (NdotL > 0.0h) ? pow(NdotH, _Shininess * 128.0) * gloss : 0.0h;

                half3 lightAtten = mainLight.color * mainLight.shadowAttenuation;
                half3 diffuse = albedo * lightAtten * NdotL;
                half3 specular = _SpecColor.rgb * lightAtten * specTerm;
                half3 ambient = SampleSH(worldNormal) * albedo;

                // Additional realtime point/spot lights -- the original
                // surface shader gets these for free from its auto-generated
                // ForwardAdd pass, using the exact same BlinnPhong formula
                // per light as the main light above.
                #if defined(_ADDITIONAL_LIGHTS)
                uint additionalLightsCount = GetAdditionalLightsCount();
                for (uint lightIndex = 0u; lightIndex < additionalLightsCount; lightIndex++)
                {
                    Light addLight = GetAdditionalLight(lightIndex, IN.positionWS);
                    half3 addAtten = addLight.color * addLight.distanceAttenuation * addLight.shadowAttenuation;
                    half addNdotL = saturate(dot(worldNormal, addLight.direction));
                    half3 addHalfDir = normalize(addLight.direction + viewDirWS);
                    half addNdotH = saturate(dot(worldNormal, addHalfDir));
                    half addSpecTerm = (addNdotL > 0.0h) ? pow(addNdotH, _Shininess * 128.0) * gloss : 0.0h;

                    diffuse += albedo * addAtten * addNdotL;
                    specular += _SpecColor.rgb * addAtten * addSpecTerm;
                }
                #endif

                half3 color = diffuse + specular + ambient;
                color = MixFog(color, IN.fogFactor);

                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            // Without this, URP's Forward+ renderer never writes this
            // shader's geometry into the depth texture (it also needs early
            // depth for its light-culling structure) -- _CameraDepthTexture
            // is left at its cleared/far value wherever this shader draws,
            // which any full-screen effect reading depth (e.g. custom fog)
            // then misreads as empty sky.
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
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
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/Shaders/UnlitDepthNormalsPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct ShadowVaryings
            {
                float4 positionHCS : SV_POSITION;
            };

            float4 GetShadowPositionHClip(ShadowAttributes IN)
            {
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #endif

                return positionCS;
            }

            ShadowVaryings ShadowVert(ShadowAttributes IN)
            {
                ShadowVaryings OUT;
                OUT.positionHCS = GetShadowPositionHClip(IN);
                return OUT;
            }

            half4 ShadowFrag(ShadowVaryings IN) : SV_TARGET
            {
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}

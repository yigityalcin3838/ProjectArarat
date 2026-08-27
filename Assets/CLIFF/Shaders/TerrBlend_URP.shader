// URP port of "Nature/Terrain/TerrBlend" (TerrBlend.shader). Same property
// names as the original so existing materials (Cliff_terrain_1/2) keep their
// texture/color assignments when their Shader is switched to this one.
Shader "Custom/URP/TerrBlend"
{
    Properties
    {
        _Mode("MODE", Range(1, 10)) = 1
        _Color ("Main Color", Color) = (1,1,1,1)
        _ColorGrass0 ("Grass0", Color) = (1,1,1,1)
        _ColorGrass1 ("Grass1", Color) = (1,1,1,1)
        _ColorGrass2 ("Grass2", Color) = (1,1,1,1)
        _ColorGrass3 ("Grass3", Color) = (1,1,1,1)
        _OldGrass("OldGrass", Range(3, 60)) = 14

        _ColorStones1 ("Stones1", Color) = (1,1,1,1)
        _ColorStones2 ("Stones2", Color) = (1,1,1,1)
        _ColorStones3 ("Stones2", Color) = (1,1,1,1)

        _SpecColor ("Specular Color", Color) = (0.5, 0.5, 0.5, 1)
        _Shininess ("Shininess", Range (0.01, 0.2)) = 0.01

        [HideInInspector] _Control ("Control (RGBA)", 2D) = "red" {}
        _Mask1 ("Mask1 (RGBA)", 2D) = "red" {}

        [HideInInspector] _Splat3 ("Layer 3 (A)", 2D) = "black" {}
        [HideInInspector] _Splat2 ("Layer 2 (B)", 2D) = "black" {}
        [HideInInspector] _Splat1 ("Layer 1 (G)", 2D) = "black" {}
        [HideInInspector] _Splat0 ("Layer 0 (R)", 2D) = "white" {}
        [HideInInspector] _Normal3 ("Normal 3 (A)", 2D) = "bump" {}
        [HideInInspector] _Normal2 ("Normal 2 (B)", 2D) = "bump" {}
        [HideInInspector] _Normal1 ("Normal 1 (G)", 2D) = "bump" {}
        [HideInInspector] _Normal0 ("Normal 0 (R)", 2D) = "bump" {}
        [HideInInspector] _MainTex ("BaseMap (RGB)", 2D) = "white" {}

        _SnowHeight ("SnowHeight", Range (-0.7, 0.19)) = 0

        _ColorTex ("ColorMap (RGB)", 2D) = "black" {}
        _Normalmap ("Normalmap (RGB)", 2D) = "white" {}
        _Tiling ("Tiling", Range (0.01, 80)) = 0.05

        _HeightSplatAll ("Grass(R) Cliff(G) Stones(B) Snow(a)", 2D) = "black" {}
        _Parallax ("Height", Range (0.005, 0.08)) = 0.02

        _Cube ("Reflection Cubemap", Cube) = "" {}
        _ReflectColor ("Reflection Color", Color) = (1,1,1,0.5)
    }

    SubShader
    {
        Tags { "Queue"="Geometry-100" "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float _Mode;
            float4 _Color;
            float4 _ColorGrass0;
            float4 _ColorGrass1;
            float4 _ColorGrass2;
            float4 _ColorGrass3;
            float _OldGrass;
            float4 _ColorStones1;
            float4 _ColorStones2;
            float4 _ColorStones3;
            float4 _SpecColor;
            float _Shininess;
            float4 _Mask1_ST;
            float4 _Splat0_ST;
            float4 _Splat1_ST;
            float4 _Splat2_ST;
            float4 _Splat3_ST;
            float _SnowHeight;
            float _Tiling;
            float _Parallax;
            float4 _ReflectColor;
        CBUFFER_END

        TEXTURE2D(_Control); SAMPLER(sampler_Control);
        TEXTURE2D(_Mask1); SAMPLER(sampler_Mask1);
        TEXTURE2D(_Splat0); SAMPLER(sampler_Splat0);
        TEXTURE2D(_Splat1); SAMPLER(sampler_Splat1);
        TEXTURE2D(_Splat2); SAMPLER(sampler_Splat2);
        TEXTURE2D(_Splat3); SAMPLER(sampler_Splat3);
        TEXTURE2D(_Normal0); SAMPLER(sampler_Normal0);
        TEXTURE2D(_Normal1); SAMPLER(sampler_Normal1);
        TEXTURE2D(_Normal2); SAMPLER(sampler_Normal2);
        TEXTURE2D(_Normal3); SAMPLER(sampler_Normal3);
        TEXTURE2D(_ColorTex); SAMPLER(sampler_ColorTex);
        TEXTURE2D(_Normalmap); SAMPLER(sampler_Normalmap);
        TEXTURE2D(_HeightSplatAll); SAMPLER(sampler_HeightSplatAll);
        TEXTURECUBE(_Cube); SAMPLER(sampler_Cube);

        // Matches Built-in's UnityCG.cginc ParallaxOffset exactly.
        float2 ParallaxOffsetBuiltin(half h, half height, half3 viewDirTS)
        {
            h = h * height - height / 2.0;
            float3 v = normalize(viewDirTS);
            v.z += 0.42;
            return h * (v.xy / v.z);
        }
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

                // The original vertex function throws away the mesh's own
                // tangent and rebuilds one purely from the object-space
                // normal: v.tangent.xyz = cross(v.normal, float3(0,0,1));
                // v.tangent.w = -1. Replicated exactly here.
                float3 tangentOS = cross(IN.normalOS, float3(0, 0, 1));
                float4 tangentOSw = float4(tangentOS, -1);

                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(IN.normalOS, tangentOSw);

                OUT.positionHCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS = normInputs.normalWS;
                OUT.tangentWS = float4(normInputs.tangentWS, tangentOSw.w);
                OUT.uv = IN.uv;
                OUT.fogFactor = ComputeFogFactor(posInputs.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 normalWS = normalize(IN.normalWS);
                float3 tangentWS = normalize(IN.tangentWS.xyz);
                float3 bitangentWS = cross(normalWS, tangentWS) * IN.tangentWS.w;

                half3 viewDirWS = normalize(GetWorldSpaceViewDir(IN.positionWS));
                // World-space view dir projected into the tangent basis --
                // matches a surface shader's tangent-space IN.viewDir.
                half3 viewDirTS = normalize(half3(
                    dot(viewDirWS, tangentWS),
                    dot(viewDirWS, bitangentWS),
                    dot(viewDirWS, normalWS)));

                float2 uvMask1 = TRANSFORM_TEX(IN.uv, _Mask1);
                float2 uvSplat0 = TRANSFORM_TEX(IN.uv, _Splat0);
                float2 uvSplat1 = TRANSFORM_TEX(IN.uv, _Splat1);
                float2 uvSplat2 = TRANSFORM_TEX(IN.uv, _Splat2);
                float2 uvSplat3 = TRANSFORM_TEX(IN.uv, _Splat3);

                // Parallax offsets per layer (identical order/scale to the original).
                half h = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat3).a;
                float2 offset = ParallaxOffsetBuiltin(h, _Parallax, viewDirTS);
                uvSplat3 += offset * 5;

                half hgrass = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat0).r;
                float2 offset1 = ParallaxOffsetBuiltin(hgrass, _Parallax, viewDirTS);
                uvSplat0 += offset1 * 2;

                half h2 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat1).g;
                float2 offset2 = ParallaxOffsetBuiltin(h2, _Parallax, viewDirTS);
                uvSplat1 += offset2 * 5;

                half h3 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat2).b;
                float2 offset3 = ParallaxOffsetBuiltin(h3, _Parallax, viewDirTS);
                uvSplat2 += offset3 * 2;

                half4 ColorTex = SAMPLE_TEXTURE2D(_ColorTex, sampler_ColorTex, uvMask1);
                half4 MaskTex = SAMPLE_TEXTURE2D(_Mask1, sampler_Mask1, uvMask1);
                half4 ControlTex = SAMPLE_TEXTURE2D(_Control, sampler_Control, uvMask1);

                // Triplanar blend factor from the (unperturbed) geometric world normal.
                float3 n = normalWS;
                float3 projNormal = saturate(pow(abs(n) * 1.4, 30));

                // World-position triplanar samples of _Splat1 (the cliff face layer).
                float4 xTex = SAMPLE_TEXTURE2D(_Splat1, sampler_Splat1, frac(IN.positionWS.zy / _Tiling));
                float4 yTex = SAMPLE_TEXTURE2D(_Splat1, sampler_Splat1, frac(IN.positionWS.zx / _Tiling));
                float4 zTex = SAMPLE_TEXTURE2D(_Splat1, sampler_Splat1, frac(IN.positionWS.xy / _Tiling));

                half4 Detail0 = SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, uvSplat0);
                half4 Detail1 = SAMPLE_TEXTURE2D(_Splat1, sampler_Splat1, IN.positionWS.zy / _Tiling);
                half4 Detail3 = SAMPLE_TEXTURE2D(_Splat3, sampler_Splat3, uvSplat3);

                // Grass ------------------------------------------------
                float4 textureGrass0 = SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, uvSplat0) * _ColorGrass0;
                float4 textureGrass1 = SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, uvSplat0) * _ColorGrass1 * ColorTex;
                float4 textureGrass2 = SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, uvSplat0) * _ColorGrass2 * ColorTex;
                float4 textureGrass3 = SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, uvSplat0) * _ColorGrass3 * ColorTex;
                // Stones -----------------------------------------------
                float4 textureStones1 = SAMPLE_TEXTURE2D(_Splat2, sampler_Splat2, uvSplat2 * 2.8) * _ColorStones1;
                float4 textureStones2 = SAMPLE_TEXTURE2D(_Splat2, sampler_Splat2, uvSplat2) * _ColorStones2;
                float4 textureStones3 = SAMPLE_TEXTURE2D(_Splat3, sampler_Splat3, uvSplat2) * _ColorStones3;

                float a00 = MaskTex.r * ColorTex.a * Detail0.a;
                float a0 = MaskTex.r;
                float a1 = MaskTex.g + ControlTex.g;
                float a2 = MaskTex.b + ControlTex.b * 2;
                float a3 = MaskTex.a - 0.6 + ControlTex.a;

                half HeightSplatTex1 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat0).r;
                half HeightSplatTex2 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat1).g;
                half HeightSplatTex3 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat2).b;
                half HeightSplatTex4 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat3 - offset).a + _SnowHeight + (_Mode / 20);

                half HeightSplatGrass0 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat0).r;
                half HeightSplatGrass1 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat0).r;
                half HeightSplatGrass2 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat1).r + 0.5;
                half HeightSplatGrass3 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat2).r + 0.6;

                half HeightSplatStones1 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat1).b + 0.3;
                half HeightSplatStones2 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat2).b + 0.3 * 2;
                half HeightSplatStones3 = SAMPLE_TEXTURE2D(_HeightSplatAll, sampler_HeightSplatAll, uvSplat3).b;

                float mgrass = max(max(max(HeightSplatGrass0 + a00 * _OldGrass * (_Mode * 0.5), HeightSplatGrass1 + a0), HeightSplatGrass2 + a1 * 8), HeightSplatGrass3 + a2 * 5) - 0.05;
                float mStones = max(max(HeightSplatStones1 + a1, HeightSplatStones2 + a2), HeightSplatStones3 + a3) - 0.05;

                float g00 = max(HeightSplatGrass0 + a00 * _OldGrass * (_Mode * 0.5) - mgrass, 0);
                float g0 = max(HeightSplatGrass1 + a0 - mgrass, 0);
                float g1 = max(HeightSplatGrass2 + a1 * 8 - mgrass, 0);
                float g2 = max(HeightSplatGrass3 + a2 * 5 - mgrass, 0);

                float s0 = max(HeightSplatStones1 + a1 - mStones, 0);
                float s1 = max(HeightSplatStones2 + a2 - mStones, 0);
                float s2 = max(HeightSplatStones3 + a3 - mStones, 0);

                float grassSum = max(g00 + g0 + g1 + g2, 1e-5);
                float4 texGrass = (textureGrass0 * g00 + textureGrass1 * g0 + textureGrass2 * g1 + textureGrass3 * g2) / grassSum;

                float stonesSum = max(s0 + s1 + s2, 1e-5);
                float4 texStones = (textureStones1 * s0 + textureStones2 * s1 + textureStones3 * s2) / stonesSum;

                float ma = max(max(max(HeightSplatTex1 + a0, HeightSplatTex2 + a1), HeightSplatTex3 + a2), HeightSplatTex4 + a3) - 0.05;

                float b0 = max(HeightSplatTex1 + a0 - ma, 0);
                float b1 = max(HeightSplatTex2 + a1 - ma, 0) * 50;
                float b2 = max(HeightSplatTex3 + a2 - ma, 0);
                float b3 = max(HeightSplatTex4 + a3 - ma, 0) * 4;
                float bSum = max(b0 + b1 * 5 + b2 + b3, 1e-5);

                float4 texture0 = texGrass;
                float4 texture1 = Detail1;
                float4 texture2 = texStones;
                float4 texture3 = Detail3;
                half4 tex = (texture0 * b0 + texture1 * b1 * 5 + texture2 * b2 + texture3 * b3) / bSum;

                float4 n0 = SAMPLE_TEXTURE2D(_Normal0, sampler_Normal0, uvSplat0);
                float4 n1 = SAMPLE_TEXTURE2D(_Normal1, sampler_Normal1, IN.positionWS.zy / _Tiling);
                float4 n2 = SAMPLE_TEXTURE2D(_Normal2, sampler_Normal2, uvSplat2);
                float4 n3 = SAMPLE_TEXTURE2D(_Normal3, sampler_Normal3, uvSplat3);
                float nSum = max(b0 + b1 + b2 + b3, 1e-5);
                float4 mixnormal = (n0 * b0 + n1 * b1 + n2 * b2 + n3 * b3) / nSum;

                half3 normalmapTex = UnpackNormal(SAMPLE_TEXTURE2D(_Normalmap, sampler_Normalmap, uvMask1));
                half3 tangentNormal = normalize(UnpackNormal(mixnormal) * 1.5 + normalmapTex * 0.2);

                half3 worldNormal = normalize(
                    tangentNormal.x * tangentWS +
                    tangentNormal.y * bitangentWS +
                    tangentNormal.z * normalWS);

                half3 albedo = zTex.rgb;
                albedo = lerp(albedo, xTex.rgb, projNormal.x);
                albedo = lerp(albedo, yTex.rgb, projNormal.y);
                albedo = lerp(tex.rgb, albedo, a1) * _Color.rgb;

                half gloss = 0.1 * MaskTex.a;
                half alpha = _Color.a;

                // Reflection cubemap, combined into Emission exactly as the
                // original -- except clamped. The original's raw x10/x20
                // multipliers are only gated by texture alpha channels that,
                // on the real Ground textures this material actually uses,
                // read close to 1 almost everywhere (no dedicated gloss/
                // sparkle mask was ever authored into them), so uncapped this
                // reflects the sky at 10-20x its real brightness as pure
                // ADDITIVE emission -- visible as the disconnected-looking
                // white patches showing up wherever any dirt/snow blend
                // weight is present, regardless of actual local lighting.
                half3 reflectDirWS = reflect(-viewDirWS, worldNormal);
                half4 reflcolDirt = SAMPLE_TEXTURECUBE(_Cube, sampler_Cube, reflectDirWS) * 10;
                reflcolDirt *= ColorTex.a;
                reflcolDirt *= b2;
                reflcolDirt *= b1;
                reflcolDirt *= SAMPLE_TEXTURE2D(_ColorTex, sampler_ColorTex, uvMask1);
                reflcolDirt *= SAMPLE_TEXTURE2D(_Mask1, sampler_Mask1, uvMask1).a;
                reflcolDirt.rgb = min(reflcolDirt.rgb, 1.0h);

                half4 reflcolSnow = SAMPLE_TEXTURECUBE(_Cube, sampler_Cube, reflectDirWS) * 20;
                reflcolSnow *= SAMPLE_TEXTURE2D(_Splat3, sampler_Splat3, uvSplat3 / 1.5).a * SAMPLE_TEXTURE2D(_Splat3, sampler_Splat3, uvSplat3 / 2 + _Time.y / 800).a;
                reflcolSnow *= b3;
                reflcolSnow.rgb = min(reflcolSnow.rgb, 1.0h);

                half3 emission = reflcolDirt.rgb + reflcolSnow.rgb;

                // Lighting -- same BlinnPhong formula as the Background
                // Mountains port: spec = pow(NdotH, Specular*128) * Gloss,
                // main light + additional lights, then ambient + emission.
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(IN.positionWS));
                half3 halfDir = normalize(mainLight.direction + viewDirWS);
                half NdotL = saturate(dot(worldNormal, mainLight.direction));
                half NdotH = saturate(dot(worldNormal, halfDir));
                // Gated off (not just scaled) when facing away from the
                // light -- see the identical comment in
                // Background_mountains_URP.shader for why.
                half specTerm = (NdotL > 0.0h) ? pow(NdotH, _Shininess * 128.0) * gloss : 0.0h;

                half3 lightAtten = mainLight.color * mainLight.shadowAttenuation;
                half3 diffuse = albedo * lightAtten * NdotL;
                half3 specular = _SpecColor.rgb * lightAtten * specTerm;

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

                half3 ambient = SampleSH(worldNormal) * albedo;

                half3 color = diffuse + specular + ambient + emission;
                color = MixFog(color, IN.fogFactor);

                return half4(color, alpha);
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

    Fallback "Universal Render Pipeline/Lit"
}

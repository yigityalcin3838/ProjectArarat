Shader "Custom/EnvironmentPBR"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Color", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color Tint", Color) = (1,1,1,1)

        [Normal] _NormalMap("Normal Map", 2D) = "bump" {}
        _NormalStrength("Normal Strength", Range(0, 2)) = 1

        _MetallicMap("Metallic (R)", 2D) = "black" {}
        _Metallic("Metallic", Range(0, 1)) = 0

        _RoughnessMap("Roughness (R)", 2D) = "grey" {}
        _Roughness("Roughness", Range(0, 1)) = 1

        _AOMap("Ambient Occlusion (R)", 2D) = "white" {}
        _AOStrength("AO Strength", Range(0, 1)) = 1

        _HeightMap("Height Map (R)", 2D) = "grey" {}
        [Toggle(_PARALLAX_ON)] _UseParallax("Enable Parallax", Float) = 1
        _ParallaxStrength("Parallax Strength", Range(0, 0.1)) = 0.02
        _ParallaxMinSteps("Parallax Min Steps", Range(1, 32)) = 4
        _ParallaxMaxSteps("Parallax Max Steps", Range(1, 64)) = 20

        _Tiling("Tiling", Float) = 1
        [Toggle(_NOTILE_ON)] _UseNoTile("Break Up Tiling (Stochastic Sampling)", Float) = 1

        _BlendSharpness("Triplanar Blend Sharpness", Range(1, 32)) = 4

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
            #pragma vertex EnvVertex
            #pragma fragment EnvFragment

            #pragma shader_feature_local _PARALLAX_ON
            #pragma shader_feature_local _NOTILE_ON

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS

            // Forward+ keeps its lights in a screen-space cluster grid, and the
            // light loop only reads that grid when this keyword is on. Without
            // it the shader still compiles -- into the older per-object path,
            // which asks for a per-object light list that Forward+ never fills
            // in. The loop then runs zero times and the surface is lit by the
            // directional light and nothing else, silently and with no error.
            //
            // This project's renderer is Forward+ (PC_Renderer, Rendering Path),
            // so without this line every point and spot light in the scene is
            // invisible to this shader.
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            // Lets the surface receive the SSAO the renderer already computes.
            // Same failure shape as above: absent, the ambient occlusion texture
            // is produced every frame and then simply never read here.
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

            TEXTURE2D(_BaseMap);     SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);   SAMPLER(sampler_NormalMap);
            TEXTURE2D(_MetallicMap); SAMPLER(sampler_MetallicMap);
            TEXTURE2D(_RoughnessMap);SAMPLER(sampler_RoughnessMap);
            TEXTURE2D(_AOMap);       SAMPLER(sampler_AOMap);
            TEXTURE2D(_HeightMap);   SAMPLER(sampler_HeightMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _NormalStrength;
                half  _Metallic;
                half  _Roughness;
                half  _AOStrength;
                float _ParallaxStrength;
                float _ParallaxMinSteps;
                float _ParallaxMaxSteps;
                float _Tiling;
                float _BlendSharpness;
            CBUFFER_END

            Varyings EnvVertex(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(IN.positionOS.xyz);

                OUT.positionCS = positionInputs.positionCS;
                OUT.positionWS = positionInputs.positionWS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                return OUT;
            }

            // ---- Triplanar blend weights ------------------------------------------------
            float3 GetTriplanarWeights(float3 normalWS)
            {
                float3 w = pow(abs(normalWS), _BlendSharpness);
                return w / max(w.x + w.y + w.z, 1e-5);
            }

            // ---- Stochastic ("no-tile") sampling --------------------------------------
            // Inigo Quilez's technique: hash each of the 4 grid cells around uv into a
            // random offset + mirror, sample the texture through each of those 4 local
            // transforms (with proper screen-space derivatives so mip/aniso filtering
            // still works), then blend with a smoothstepped cell-interior weight. Every
            // map is sampled through the SAME hashed cell layout (computed once, reused
            // for color/normal/metallic/roughness/AO) so they all stay in registration.

            struct NoTileUVs
            {
                float2 uva, uvb, uvc, uvd;
                float2 ddxa, ddya, ddxb, ddyb, ddxc, ddyc, ddxd, ddyd;
                float2 signA, signB, signC, signD;
                float2 blend;
            };

            float4 NoTileHash(float2 p)
            {
                float4 dots = float4(
                    dot(p, float2(127.1, 311.7)),
                    dot(p, float2(269.5, 183.3)),
                    dot(p, float2(419.2, 371.9)),
                    dot(p, float2(148.3, 254.1)));
                return frac(sin(dots) * 43758.5453);
            }

            NoTileUVs ComputeNoTileUVs(float2 uv)
            {
                NoTileUVs t = (NoTileUVs)0;

                float2 iuv = floor(uv);
                float2 fuv = frac(uv);

                float4 ofa = NoTileHash(iuv + float2(0, 0));
                float4 ofb = NoTileHash(iuv + float2(1, 0));
                float4 ofc = NoTileHash(iuv + float2(0, 1));
                float4 ofd = NoTileHash(iuv + float2(1, 1));

                float2 ddxUv = ddx(uv);
                float2 ddyUv = ddy(uv);

                t.signA = sign(ofa.zw - 0.5);
                t.signB = sign(ofb.zw - 0.5);
                t.signC = sign(ofc.zw - 0.5);
                t.signD = sign(ofd.zw - 0.5);

                t.uva = uv * t.signA + ofa.xy; t.ddxa = ddxUv * t.signA; t.ddya = ddyUv * t.signA;
                t.uvb = uv * t.signB + ofb.xy; t.ddxb = ddxUv * t.signB; t.ddyb = ddyUv * t.signB;
                t.uvc = uv * t.signC + ofc.xy; t.ddxc = ddxUv * t.signC; t.ddyc = ddyUv * t.signC;
                t.uvd = uv * t.signD + ofd.xy; t.ddxd = ddxUv * t.signD; t.ddyd = ddyUv * t.signD;

                t.blend = smoothstep(0.25, 0.75, fuv);
                return t;
            }

            half4 SampleNoTile(TEXTURE2D_PARAM(tex, samp), NoTileUVs t)
            {
                half4 cola = SAMPLE_TEXTURE2D_GRAD(tex, samp, t.uva, t.ddxa, t.ddya);
                half4 colb = SAMPLE_TEXTURE2D_GRAD(tex, samp, t.uvb, t.ddxb, t.ddyb);
                half4 colc = SAMPLE_TEXTURE2D_GRAD(tex, samp, t.uvc, t.ddxc, t.ddyc);
                half4 cold = SAMPLE_TEXTURE2D_GRAD(tex, samp, t.uvd, t.ddxd, t.ddyd);
                return lerp(lerp(cola, colb, t.blend.x), lerp(colc, cold, t.blend.x), t.blend.y);
            }

            // Mirroring the UVs (the sign flip above) also mirrors the tangent-space
            // normal's X/Y -- undo that per tap before blending, then renormalize.
            half3 SampleNoTileNormal(TEXTURE2D_PARAM(tex, samp), NoTileUVs t, float scale)
            {
                half3 na = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(tex, samp, t.uva, t.ddxa, t.ddya), scale); na.xy *= t.signA;
                half3 nb = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(tex, samp, t.uvb, t.ddxb, t.ddyb), scale); nb.xy *= t.signB;
                half3 nc = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(tex, samp, t.uvc, t.ddxc, t.ddyc), scale); nc.xy *= t.signC;
                half3 nd = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(tex, samp, t.uvd, t.ddxd, t.ddyd), scale); nd.xy *= t.signD;
                half3 n = lerp(lerp(na, nb, t.blend.x), lerp(nc, nd, t.blend.x), t.blend.y);
                return normalize(n);
            }

            // ---- Parallax occlusion mapping --------------------------------------------
            // Regular (tiled) height sampling on purpose -- running the 4-tap no-tile
            // sampler inside a multi-step ray march would multiply an already-expensive
            // loop by 4x for a channel where repetition is far less noticeable than on
            // color/normal.
            // axisWeight: this axis's triplanar blend weight (0-1). On an axis-aligned
            // surface one axis dominates (weight ~1) and this is a no-op. On an inclined
            // surface two or three axes blend together, and each axis parallaxes the
            // surface toward ITS OWN "up" independently and correctly in isolation -- but
            // blending multiple independently-displaced illusions doesn't add up to one
            // coherent displaced surface, which is what actually causes the warped/swimming
            // look on slopes (a known limitation of combining triplanar with parallax).
            // Scaling each axis's offset by its own dominance means axes only push hard
            // where they're already the main contributor, so conflicting displacement from
            // a blended second/third axis is proportionally weaker instead of full-strength.
            float2 ParallaxOffset(float2 uv, float3 viewDirTS, float axisWeight)
            {
                float numSteps = lerp(_ParallaxMaxSteps, _ParallaxMinSteps, saturate(abs(viewDirTS.z)));
                float stepSize = 1.0 / numSteps;

                float2 uvDelta = (viewDirTS.xy / max(abs(viewDirTS.z), 0.05)) * _ParallaxStrength * axisWeight / numSteps;

                float2 currentUV = uv;
                float currentDepth = 0.0;
                float currentHeight = 1.0 - SAMPLE_TEXTURE2D_LOD(_HeightMap, sampler_HeightMap, currentUV, 0).r;

                float2 prevUV = currentUV;
                float prevHeight = currentHeight;
                float prevDepth = currentDepth;

                int maxIterations = (int)_ParallaxMaxSteps;
                [loop]
                for (int i = 0; i < maxIterations; i++)
                {
                    if (currentDepth >= currentHeight || i >= (int)numSteps)
                        break;

                    prevUV = currentUV;
                    prevHeight = currentHeight;
                    prevDepth = currentDepth;

                    currentUV -= uvDelta;
                    currentHeight = 1.0 - SAMPLE_TEXTURE2D_LOD(_HeightMap, sampler_HeightMap, currentUV, 0).r;
                    currentDepth += stepSize;
                }

                float afterDepth = currentDepth - currentHeight;
                float beforeDepth = prevHeight - prevDepth;
                float weight = afterDepth / max(afterDepth - beforeDepth, 1e-5);

                return lerp(currentUV, prevUV, saturate(weight));
            }

            // ---- Per-axis channel sample -------------------------------------------------
            // One projection's worth of the full PBR channel set (albedo/normal/metallic/
            // roughness/AO), with parallax + no-tile applied internally so the triplanar
            // blend below just has to combine three of these by axis weight.
            struct AxisSample
            {
                half3 albedo;
                half3 normalTS;
                half  metallic;
                half  roughness;
                half  ao;
            };

            AxisSample SampleAxis(float2 uv, float3 viewDirTS, float axisWeight)
            {
                AxisSample s = (AxisSample)0;

                #if defined(_PARALLAX_ON)
                    uv = ParallaxOffset(uv, viewDirTS, axisWeight);
                #endif

                #if defined(_NOTILE_ON)
                    NoTileUVs t = ComputeNoTileUVs(uv);
                    s.albedo = SampleNoTile(TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap), t).rgb * _BaseColor.rgb;
                    s.normalTS = SampleNoTileNormal(TEXTURE2D_ARGS(_NormalMap, sampler_NormalMap), t, _NormalStrength);
                    s.metallic = SampleNoTile(TEXTURE2D_ARGS(_MetallicMap, sampler_MetallicMap), t).r;
                    s.roughness = SampleNoTile(TEXTURE2D_ARGS(_RoughnessMap, sampler_RoughnessMap), t).r;
                    s.ao = SampleNoTile(TEXTURE2D_ARGS(_AOMap, sampler_AOMap), t).r;
                #else
                    s.albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb * _BaseColor.rgb;
                    s.normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv), _NormalStrength);
                    s.metallic = SAMPLE_TEXTURE2D(_MetallicMap, sampler_MetallicMap, uv).r;
                    s.roughness = SAMPLE_TEXTURE2D(_RoughnessMap, sampler_RoughnessMap, uv).r;
                    s.ao = SAMPLE_TEXTURE2D(_AOMap, sampler_AOMap, uv).r;
                #endif

                return s;
            }

            half4 EnvFragment(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                float3 normalWS = normalize(IN.normalWS);
                float3 viewDirWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                float3 blendWeights = GetTriplanarWeights(normalWS);

                float2 uvX = IN.positionWS.zy * _Tiling;
                float2 uvY = IN.positionWS.xz * _Tiling;
                float2 uvZ = IN.positionWS.xy * _Tiling;

                // Permute the world view direction into each projection's local
                // (tangent, bitangent, normal) axes so parallax marches the right way.
                float3 viewTSX = float3(viewDirWS.z, viewDirWS.y, viewDirWS.x);
                float3 viewTSY = float3(viewDirWS.x, viewDirWS.z, viewDirWS.y);
                float3 viewTSZ = float3(viewDirWS.x, viewDirWS.y, viewDirWS.z);

                AxisSample sx = SampleAxis(uvX, viewTSX, blendWeights.x);
                AxisSample sy = SampleAxis(uvY, viewTSY, blendWeights.y);
                AxisSample sz = SampleAxis(uvZ, viewTSZ, blendWeights.z);

                half3 albedo = sx.albedo * blendWeights.x + sy.albedo * blendWeights.y + sz.albedo * blendWeights.z;
                half metallicSample = sx.metallic * blendWeights.x + sy.metallic * blendWeights.y + sz.metallic * blendWeights.z;
                half roughnessSample = sx.roughness * blendWeights.x + sy.roughness * blendWeights.y + sz.roughness * blendWeights.z;
                half aoSample = sx.ao * blendWeights.x + sy.ao * blendWeights.y + sz.ao * blendWeights.z;

                // Whiteout-blend the three tangent-space normals against the base world
                // normal, then swizzle each back into world space before combining.
                half3 tnormalX = half3(sx.normalTS.xy + normalWS.zy, normalWS.x);
                half3 tnormalY = half3(sy.normalTS.xy + normalWS.xz, normalWS.y);
                half3 tnormalZ = half3(sz.normalTS.xy + normalWS.xy, normalWS.z);

                float3 finalNormalWS = normalize(
                    tnormalX.zyx * blendWeights.x +
                    tnormalY.xzy * blendWeights.y +
                    tnormalZ.xyz * blendWeights.z);

                half metallic = metallicSample * _Metallic;
                half roughness = saturate(roughnessSample * _Roughness);
                half smoothness = 1.0h - roughness;
                half occlusion = lerp(1.0h, aoSample, _AOStrength);

                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.positionCS = IN.positionCS;
                inputData.normalWS = finalNormalWS;
                inputData.viewDirectionWS = viewDirWS;
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord = IN.fogFactor;
                inputData.vertexLighting = half3(0, 0, 0);
                inputData.bakedGI = EvaluateAmbientProbeSRGB(finalNormalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                half4 color = UniversalFragmentPBR(inputData, albedo, metallic, half3(0, 0, 0),
                    smoothness, occlusion, half3(0, 0, 0), 1.0h);

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

        // Required, not optional, whenever anything in the frame requests
        // normals alongside depth -- which SSAO does when its Source is set
        // to "Depth Normals". In that configuration URP runs a DepthNormals
        // prepass and writes _CameraDepthTexture DIRECTLY from it (there is
        // no separate depth-only prepass and no depth copy: see
        // CalculateDepthCopySchedules in UniversalRendererRenderGraph.cs --
        // "the prepass will render directly to the depthTexture", so the
        // copy is skipped). A shader with no DepthNormals pass therefore
        // contributes NOTHING to the depth texture, leaving those pixels at
        // the cleared far value -- which any depth-reading full-screen
        // effect then misreads as empty sky.
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

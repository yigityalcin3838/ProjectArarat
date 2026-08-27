// Replaces URP's built-in SSAO result at Player-mask pixels with a
// custom-computed occlusion value, and passes the built-in result through
// unchanged everywhere else. This is what actually makes Layer "Player"
// exempt from built-in SSAO: URP's SSAO pass writes its result into the
// GLOBAL texture _ScreenSpaceOcclusionTexture (sampled later by every
// opaque object's lighting), and this pass overwrites that same global
// texture with a corrected version -- it doesn't touch SSAO's own settings
// or code.
//
// The custom AO itself is a standard world-space hemisphere-sample SSAO:
// for each of a handful of random directions in the hemisphere above the
// surface normal, project a hypothetical point at _PlayerAORadius out along
// that direction back to screen space and compare its distance from the
// camera against whatever REAL surface the depth buffer reports at that
// same screen position. If the real surface is closer to the camera than
// the hypothetical point, something is in the way -- that's occlusion.
Shader "Hidden/PostProcessing/PlayerAO"
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
            Name "PlayerAO"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // Set globally by URP's own SSAO pass before this one runs --
            // sampled here as an ordinary global texture (not threaded
            // through RenderGraph as an explicit resource, the same way
            // this project's other full-screen passes read built-in
            // globals like _CameraDepthTexture).
            TEXTURE2D(_ScreenSpaceOcclusionTexture); SAMPLER(sampler_ScreenSpaceOcclusionTexture);
            TEXTURE2D(_PlayerMaskTexture); SAMPLER(sampler_PlayerMaskTexture);

            float _PlayerAORadius;
            float _PlayerAOIntensity;
            float _PlayerAOPower;
            float _PlayerAOOutlineIntensity;
            float _PlayerAOOutlineThickness;

            #define PLAYER_AO_SAMPLE_COUNT 10

            float Hash1(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float2 Hash2(float2 p)
            {
                float x = Hash1(p);
                float y = Hash1(p + 19.19h);
                return float2(x, y);
            }

            half ComputePlayerAO(float2 uv, float3 positionWS, float3 normalWS)
            {
                // Build an arbitrary (per-pixel randomized) tangent frame
                // around the surface normal -- the AO estimate is an
                // average over many directions, so it doesn't need to be
                // the "real" tangent, just something to spread samples
                // evenly across the hemisphere.
                float3 randomVec = normalize(float3(
                    Hash1(uv * 13.1h + 1.7h) * 2.0h - 1.0h,
                    Hash1(uv * 7.3h + 9.1h) * 2.0h - 1.0h,
                    Hash1(uv * 3.7h + 5.3h) * 2.0h - 1.0h));
                float3 tangent = normalize(randomVec - normalWS * dot(randomVec, normalWS));
                float3 bitangent = cross(normalWS, tangent);

                half occlusion = 0.0h;

                UNITY_UNROLL
                for (int i = 0; i < PLAYER_AO_SAMPLE_COUNT; i++)
                {
                    float2 h = Hash2(uv * 971.3h + i * 57.7h);
                    float r = sqrt(h.x);
                    float theta = h.y * TWO_PI;
                    // Cosine-weighted hemisphere direction in the tangent frame.
                    float3 localDir = float3(r * cos(theta), r * sin(theta), sqrt(max(1e-4h, 1.0h - h.x)));
                    float3 sampleDirWS = tangent * localDir.x + bitangent * localDir.y + normalWS * localDir.z;
                    float3 samplePosWS = positionWS + sampleDirWS * _PlayerAORadius;

                    float2 sampleUV = ComputeNormalizedDeviceCoordinates(samplePosWS, UNITY_MATRIX_VP);
                    float sampleRawDepth = SampleSceneDepth(sampleUV);
                    float3 actualSurfacePosWS = ComputeWorldSpacePosition(sampleUV, sampleRawDepth, UNITY_MATRIX_I_VP);

                    float sampleDistToCam = distance(samplePosWS, _WorldSpaceCameraPos);
                    float actualDistToCam = distance(actualSurfacePosWS, _WorldSpaceCameraPos);

                    // A real surface closer to the camera than our
                    // hypothetical sample point means something occupies
                    // that space -- occluded. Samples whose real surface is
                    // much farther away than the sample radius are ignored
                    // (that's unrelated background, not local occlusion).
                    half isCloser = actualDistToCam < sampleDistToCam - 0.02h ? 1.0h : 0.0h;
                    half rangeCheck = saturate(_PlayerAORadius / max(abs(sampleDistToCam - actualDistToCam), 1e-4h));

                    occlusion += isCloser * rangeCheck;
                }

                occlusion /= PLAYER_AO_SAMPLE_COUNT;
                return saturate(1.0h - pow(occlusion, _PlayerAOPower) * _PlayerAOIntensity);
            }

            // Outline-style edge term: looks at the 4 neighbors a few pixels
            // out and flags a sharp silhouette/crease wherever the surface
            // normal turns sharply OR the depth jumps (relative to how far
            // away this pixel already is, so it reads the same width up
            // close and at a distance). Hemisphere sampling above needs
            // actual nearby occluding geometry to darken something -- a
            // normal that just points a different way (a sharp edge on the
            // SAME contiguous surface) doesn't reliably trigger it, which is
            // exactly the case this catches.
            half ComputeEdgeAO(float2 uv, float3 normalWS, float centerEyeDepth)
            {
                float2 texelSize = (_PlayerAOOutlineThickness / _ScreenParams.xy);

                float3 nRight = SampleSceneNormals(uv + float2(texelSize.x, 0.0h));
                float3 nLeft  = SampleSceneNormals(uv - float2(texelSize.x, 0.0h));
                float3 nUp    = SampleSceneNormals(uv + float2(0.0h, texelSize.y));
                float3 nDown  = SampleSceneNormals(uv - float2(0.0h, texelSize.y));

                float dRight = LinearEyeDepth(SampleSceneDepth(uv + float2(texelSize.x, 0.0h)), _ZBufferParams);
                float dLeft  = LinearEyeDepth(SampleSceneDepth(uv - float2(texelSize.x, 0.0h)), _ZBufferParams);
                float dUp    = LinearEyeDepth(SampleSceneDepth(uv + float2(0.0h, texelSize.y)), _ZBufferParams);
                float dDown  = LinearEyeDepth(SampleSceneDepth(uv - float2(0.0h, texelSize.y)), _ZBufferParams);

                half normalEdge = (1.0h - dot(normalWS, nRight)) + (1.0h - dot(normalWS, nLeft))
                                 + (1.0h - dot(normalWS, nUp)) + (1.0h - dot(normalWS, nDown));

                half depthEdge = abs(dRight - centerEyeDepth) + abs(dLeft - centerEyeDepth)
                                + abs(dUp - centerEyeDepth) + abs(dDown - centerEyeDepth);
                // Scale the depth jump by distance so the same relative gap
                // (not the same absolute one) counts as an edge whether the
                // player is close to the camera or far away.
                depthEdge = depthEdge / max(centerEyeDepth * 0.05h, 0.001h);

                return saturate(max(normalEdge, depthEdge));
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half builtinAO = SAMPLE_TEXTURE2D(_ScreenSpaceOcclusionTexture, sampler_ScreenSpaceOcclusionTexture, uv).r;
                half playerMask = SAMPLE_TEXTURE2D(_PlayerMaskTexture, sampler_PlayerMaskTexture, uv).r;

                // Not a Player pixel -- pass URP's own SSAO result through untouched.
                if (playerMask < 0.5h)
                    return half4(builtinAO.rrr, 1.0h);

                float rawDepth = SampleSceneDepth(uv);
                if (rawDepth <= 0.0h)
                    return half4(1.0h, 1.0h, 1.0h, 1.0h);

                float3 positionWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 normalWS = SampleSceneNormals(uv);
                float centerEyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);

                half customAO = ComputePlayerAO(uv, positionWS, normalWS);
                half edge = ComputeEdgeAO(uv, normalWS, centerEyeDepth) * _PlayerAOOutlineIntensity;

                half finalAO = saturate(customAO - edge);
                return half4(finalAO.rrr, 1.0h);
            }
            ENDHLSL
        }

        // A handful of random hemisphere samples per pixel (see above) is
        // cheap but inherently noisy -- without smoothing it reads as a
        // stippled/dirty speckle pattern instead of a soft occlusion
        // gradient. This blurs that noise away while stopping at real depth
        // edges (a bilateral blur, weighted by how close each neighbor's
        // depth is to the center pixel's), so contact shadows stay crisp
        // instead of bleeding across object silhouettes. Runs over the
        // whole result, not just the Player-masked area -- built-in SSAO
        // pixels are already smooth, so re-blurring them is a no-op in
        // practice, and skipping them would need an extra mask sample per
        // tap for no real benefit.
        Pass
        {
            Name "PlayerAOBlur"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment BlurFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            #define PLAYER_AO_BLUR_DEPTH_SHARPNESS 40.0h

            half4 BlurFrag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                // _ScreenParams always reflects the actual render target
                // size at execution time -- simpler and more reliable here
                // than trying to derive it from a RenderGraph TextureDesc,
                // which may describe a screen-relative (not fixed-size) texture.
                float2 texelSize = 1.0h / _ScreenParams.xy;

                float centerRawDepth = SampleSceneDepth(uv);
                float centerEyeDepth = LinearEyeDepth(centerRawDepth, _ZBufferParams);

                half sum = 0.0h;
                half weightSum = 0.0h;

                UNITY_UNROLL
                for (int x = -2; x <= 2; x++)
                {
                    UNITY_UNROLL
                    for (int y = -2; y <= 2; y++)
                    {
                        float2 sampleUV = uv + float2(x, y) * texelSize;
                        half ao = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, sampleUV).r;

                        float sampleRawDepth = SampleSceneDepth(sampleUV);
                        float sampleEyeDepth = LinearEyeDepth(sampleRawDepth, _ZBufferParams);

                        half depthWeight = (half)exp(-abs(sampleEyeDepth - centerEyeDepth) * PLAYER_AO_BLUR_DEPTH_SHARPNESS);
                        sum += ao * depthWeight;
                        weightSum += depthWeight;
                    }
                }

                half blurred = sum / max(weightSum, 1e-5h);
                return half4(blurred.rrr, 1.0h);
            }
            ENDHLSL
        }
    }
}

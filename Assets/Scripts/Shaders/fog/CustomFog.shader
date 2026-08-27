// Exponential Height Fog, full-screen post-process, covering both real
// geometry AND the skybox with the same physical model (unlike Unreal's or
// Flax's version of this, which only ever fog geometry).
//
// Uses the closed-form ray integral (Inigo Quilez, "Colored fog",
// https://iquilezles.org/articles/fog/) instead of sampling density only at
// the surface point: density falls off exponentially with world height,
// d(y) = density * exp(-heightFalloff * y), and the amount of fog a ray
// picks up is the INTEGRAL of that density along the whole ray from camera
// to surface, not just a single sample at the far end. For a surface at a
// finite distance that integral has an exact closed form:
//
//   fogAmount = (density / falloff) * exp(-rayOrigin.y * falloff)
//               * (1 - exp(-dist * rayDir.y * falloff)) / rayDir.y
//
// Sky pixels are identified exactly (not by a distance heuristic): the
// skybox is drawn with ZWrite Off, so on this project's reversed-Z platform
// they're always left holding the frame's cleared far-plane depth (raw
// depth 0). Rather than reusing the far clip plane as a stand-in "distance"
// for sky (which would make the sky's haziness depend on an arbitrary
// rendering setting instead of the actual fog parameters), the SAME
// integral is taken to its true distance-to-infinity limit for sky pixels:
// as dist -> infinity, the exp(-dist * rayDir.y * falloff) term vanishes
// for any ray pointing above the horizon, leaving a finite closed form; a
// ray at or below the horizon never escapes the (infinite, in this model)
// atmosphere, so the limit is just "fully fogged". That's what makes the
// horizon itself the haziest part of the sky and the zenith the clearest,
// same as real atmospheric perspective, with no separate "horizon band"
// parameter needed.
Shader "Hidden/PostProcessing/CustomFog"
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
            Name "CustomFog"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            half4 _FogColor;
            float _FogDensity;
            float _FogHeightFalloff;
            float _StartDistance;
            float _CutoffDistance;
            float _MaxOpacity;

            // Inigo Quilez's closed-form ray integral of an exponentially
            // height-varying density. rayOrigin/rayDir describe the ray
            // already advanced to wherever fog starts accumulating from
            // (see _StartDistance handling in Frag), and dist is the
            // remaining distance from there to the surface.
            float HeightFogAmount(float3 rayOrigin, float3 rayDir, float dist)
            {
                float opticalDepth;
                if (abs(rayDir.y) > 1e-4)
                {
                    opticalDepth = (_FogDensity / _FogHeightFalloff) * exp(-rayOrigin.y * _FogHeightFalloff)
                                 * (1.0 - exp(-dist * rayDir.y * _FogHeightFalloff)) / rayDir.y;
                }
                else
                {
                    // Limit of the closed form as rayDir.y -> 0: density is
                    // effectively constant along the ray at this height.
                    opticalDepth = _FogDensity * exp(-rayOrigin.y * _FogHeightFalloff) * dist;
                }
                return saturate(opticalDepth);
            }

            // Same integral, taken to its distance-to-infinity limit --
            // used for sky pixels, where there's no finite surface distance
            // to integrate up to.
            float HeightFogAmountSky(float3 rayOrigin, float3 rayDir)
            {
                if (rayDir.y > 1e-4)
                {
                    float opticalDepth = (_FogDensity / _FogHeightFalloff) * exp(-rayOrigin.y * _FogHeightFalloff) / rayDir.y;
                    return saturate(opticalDepth);
                }
                // Horizontal or below: the ray never climbs out of the
                // (infinite, in this model) atmosphere, so the path length
                // -- and so the fog -- is unbounded.
                return 1.0;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half3 screenColor = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv).rgb;

                float rawDepth = SampleSceneDepth(uv);

                // Sky test: the skybox never writes depth, so sky pixels are
                // always left at exactly the frame's cleared far value (0).
                bool isSky = rawDepth <= 0.0h;

                if (isSky && _CutoffDistance > 0.0h)
                    return half4(screenColor, 1.0h);

                float3 positionWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 toPixel = positionWS - _WorldSpaceCameraPos;

                half fogAmount;

                if (isSky)
                {
                    float3 rayDir = normalize(toPixel);
                    float3 rayOrigin = _WorldSpaceCameraPos + rayDir * _StartDistance;
                    fogAmount = (half)HeightFogAmountSky(rayOrigin, rayDir);
                }
                else
                {
                    float dist = length(toPixel);
                    if (_CutoffDistance > 0.0h && dist > _CutoffDistance)
                        return half4(screenColor, 1.0h);

                    float3 rayDir = toPixel / max(dist, 1e-5h);

                    // Shift the integral's start point along the ray by
                    // _StartDistance, using the REAL height there (not the
                    // camera's) as the new origin.
                    float3 rayOrigin = _WorldSpaceCameraPos + rayDir * _StartDistance;
                    float remainingDist = max(dist - _StartDistance, 0.0h);

                    fogAmount = (half)HeightFogAmount(rayOrigin, rayDir, remainingDist);
                }

                fogAmount = min(fogAmount, (half)_MaxOpacity);

                half3 result = lerp(screenColor, _FogColor.rgb, fogAmount);
                return half4(result, 1.0h);
            }
            ENDHLSL
        }
    }
}

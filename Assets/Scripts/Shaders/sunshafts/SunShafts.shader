// Volumetric light shafts, ray-marched against the main light's shadow map.
//
// For every pixel, a ray is walked from the camera toward whatever that pixel
// shows, and at each step the shadow map answers "is this point in space lit
// by the sun?". Adding those answers up gives how much sunlit air the ray
// passed through -- which IS the beam. Where a window or a gap in the trees
// lets light through, the air behind it accumulates and a shaft appears.
//
// This is why it works while facing away from the sun, unlike a screen-space
// radial blur: nothing here depends on the sun being visible on screen, only
// on the shadow map, which covers the space around the camera regardless of
// where it's pointed.
//
// The catch is the shadow map's range: past the pipeline's Shadow Distance
// there's nothing to test against, so shafts simply stop there.
Shader "Hidden/PostProcessing/SunShafts"
{
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            Name "SunShaftsScatter"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragScatter

            // Without these the shadow lookups below compile to "always lit"
            // and every ray comes back fully bright, which reads as a uniform
            // haze with no beams in it at all.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _Density;
            float _ForwardScattering;
            float _MaxForwardBoost;
            float _MaxDistance;
            int _Steps;

            // Local density regions. Each is a unit cube in its own local
            // space, so the matrix carries position, rotation and size at once
            // and the inside-test is just "are all axes within +/-0.5".
            #define SUNSHAFTS_MAX_DENSITY_VOLUMES 8
            float4x4 _DensityVolumeWorldToLocal[SUNSHAFTS_MAX_DENSITY_VOLUMES];
            float4 _DensityVolumeParams[SUNSHAFTS_MAX_DENSITY_VOLUMES]; // x = multiplier, y = edge falloff
            int _DensityVolumeCount;

            // Density multiplier at a world point. Evaluated per ray-march
            // step, not once from the camera -- that's what lets a beam inside
            // a room read correctly while standing outside looking in.
            //
            // The volume the point sits deepest inside wins, which handles
            // both thickening (>1) and thinning (<1) regions and gives a
            // sensible answer where two overlap.
            float DensityMultiplierAt(float3 positionWS)
            {
                float bestMultiplier = 1.0;
                float bestFade = 0.0;

                for (int v = 0; v < _DensityVolumeCount; v++)
                {
                    float3 local = mul(_DensityVolumeWorldToLocal[v], float4(positionWS, 1.0)).xyz;

                    // Distance in from the nearest face, in local units.
                    float3 toFace = 0.5 - abs(local);
                    float inside = min(min(toFace.x, toFace.y), toFace.z);
                    if (inside <= 0.0)
                        continue;

                    float fade = saturate(inside / max(_DensityVolumeParams[v].y, 1e-4));
                    if (fade > bestFade)
                    {
                        bestFade = fade;
                        bestMultiplier = _DensityVolumeParams[v].x;
                    }
                }

                return lerp(1.0, bestMultiplier, bestFade);
            }

            // Henyey-Greenstein: how much haze scatters light toward the
            // viewer for a given angle between the view ray and the light.
            // Without it beams look equally strong in every direction, which
            // outdoors means a flat wash of brightness over the whole screen
            // -- every ray is fully lit, so every pixel gets the same value,
            // and a constant added everywhere carries no shape at all. With
            // it, beams flare toward the sun and fade away from it, which is
            // both what real air does and what keeps the outdoors clean.
            //
            // Normalised so g = 0 returns exactly 1. The textbook form carries
            // a 1/4pi that makes the whole effect ~12x dimmer and, worse, makes
            // its overall brightness change as g changes -- so tuning
            // Forward Scattering would fight Density instead of being
            // independent of it.
            float HenyeyGreenstein(float cosTheta, float g)
            {
                float gSqr = g * g;
                float denom = 1.0 + gSqr - 2.0 * g * cosTheta;
                return (1.0 - gSqr) / pow(max(denom, 1e-4), 1.5);
            }

            // Cheap per-pixel dither. With only a couple dozen steps every ray
            // would otherwise start at the same place and the shared step
            // boundaries show up as hard banded shells through the beam --
            // offsetting each pixel's start scatters that into noise, which
            // the downsample and bilinear upscale then smooth away.
            float Dither(float2 uv)
            {
                return frac(sin(dot(uv, float2(12.9898, 78.233))) * 43758.5453);
            }

            half4 FragScatter(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                float rawDepth = SampleSceneDepth(uv);
                float3 surfaceWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);

                float3 rayVec = surfaceWS - _WorldSpaceCameraPos;
                float sceneDistance = length(rayVec);
                float3 rayDir = rayVec / max(sceneDistance, 1e-5);

                // Stop at whatever the ray hits first: the surface, or the end
                // of usable shadow data. Marching past geometry would light up
                // air that's actually behind a wall.
                float marchDistance = min(sceneDistance, _MaxDistance);

                int steps = max(_Steps, 1);
                float stepSize = marchDistance / steps;

                float3 stepVec = rayDir * stepSize;
                float3 position = _WorldSpaceCameraPos + stepVec * Dither(uv);

                // Capped: the phase function's peak is by far the sharpest term
                // in the whole effect, and it lands exactly where the view
                // points at the sun -- which is the bright blob there. Clamping
                // it leaves the beams alone, because those come from shadow
                // contrast along the ray rather than from viewing angle.
                float phase = HenyeyGreenstein(dot(rayDir, _MainLightPosition.xyz), _ForwardScattering);
                phase = min(phase, _MaxForwardBoost);

                // Beer-Lambert accumulation, front to back. Light scattered
                // toward the eye from a point is dimmed by everything between
                // that point and the eye, so transmittance is carried along and
                // multiplied in. Skipping this -- applying extinction once at
                // the end instead -- weights the whole ray equally, which is
                // what let a long fully-lit ray outdoors keep piling up and
                // wash the screen out.
                float transmittance = 1.0;
                float scattering = 0.0;

                for (int i = 0; i < steps; i++)
                {
                    // Extinction per metre at THIS point, so a local volume
                    // thickens only the stretch of ray actually inside it.
                    // Treated as a purely scattering haze (nothing absorbed),
                    // so the scattering coefficient equals it.
                    float sigmaE = max(_Density * DensityMultiplierAt(position), 1e-5);
                    float stepTransmittance = exp(-sigmaE * stepSize);

                    float lightVisibility = MainLightRealtimeShadow(TransformWorldToShadowCoord(position));

                    // Frostbite's energy-conserving step integral: the analytic
                    // integral of scattering across the whole step, rather than
                    // one sample multiplied by the step length. Without it the
                    // result drifts as the step count changes, so tuning
                    // Density would mean re-tuning Steps too.
                    float S = lightVisibility * sigmaE * phase;
                    float stepScattering = (S - S * stepTransmittance) / sigmaE;

                    scattering += transmittance * stepScattering;
                    transmittance *= stepTransmittance;

                    position += stepVec;
                }

                return half4(scattering.xxx, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "SunShaftsComposite"

            // Added by the hardware, so this never has to read the colour
            // target it writes to.
            Blend One One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _ShaftColor;
            float _Intensity;

            half4 FragComposite(Varyings input) : SV_Target
            {
                half scattering = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).r;

                // Tinted by the sun's own colour so the shafts shift with the
                // time of day on their own, with _ShaftColor on top as an
                // artistic override.
                half3 color = scattering * _MainLightColor.rgb * _ShaftColor.rgb * _Intensity;
                return half4(color, 1.0h);
            }
            ENDHLSL
        }
    }
}

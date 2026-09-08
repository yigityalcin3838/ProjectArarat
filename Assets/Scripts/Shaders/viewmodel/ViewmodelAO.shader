Shader "Hidden/PostProcessing/ViewmodelAO"
{
    // Ambient occlusion for the viewmodel, built the way URP builds its own.
    //
    // Two things were being asked of it, and they were two shaders before this:
    // the creases and cavities on the weapon itself, and a soft darkening that
    // spills off the weapon's silhouette into the room. Both come out of one
    // sampling loop here, because both are the same question asked at two
    // radii -- "how much weapon is near this pixel" -- and answering it once is
    // cheaper and, more to the point, keeps them consistent with each other.
    //
    // Why the previous attempts looked jagged, and what actually fixes it:
    //
    // Any screen-space occlusion is a count over a handful of samples, so its
    // raw answer only has a handful of values. Neither rotating the pattern nor
    // blurring a hard mask changes that -- the first scatters which value each
    // pixel picks, the second smooths a contour that was quantised before it
    // was smoothed. What Unity does, and what this now does, is give every
    // pixel a DIFFERENT set of samples -- different angles AND different radii,
    // from interleaved gradient noise -- so neighbouring pixels genuinely
    // disagree, and then average that disagreement away with a separable blur.
    // Twelve samples across twenty-five blurred neighbours is three hundred
    // effective samples, which is where the smoothness comes from.
    //
    // Everything is read from the overlay pass's own depth buffer, which holds
    // the weapon and nothing else. That is deliberate: the weapon is drawn over
    // the world at a projection of its own, so the world's depth is neither
    // needed here nor comparable to it.
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

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D(_ViewmodelDepthTex);
        SAMPLER(sampler_ViewmodelDepthTex);

        // Half-resolution RG: red is the weapon's own occlusion, green is how
        // much of the neighbourhood is weapon at all. The blur passes carry both
        // through together.
        TEXTURE2D(_ViewmodelAOTex);
        SAMPLER(sampler_ViewmodelAOTex);

        // Full-resolution (width, height, 1/width, 1/height). Passed in rather
        // than read from _ScreenParams because the AO pass draws into a
        // half-size target, where _ScreenParams no longer describes the screen
        // the radius settings are quoted in.
        float4 _ViewmodelScreenSize;

        // tan(fov/2) * aspect, tan(fov/2) -- for the viewmodel's own projection,
        // not the camera's. The overlay draws with an overridden field of view,
        // so reconstructing its geometry with the camera's would put every
        // surface at the wrong angle and tilt all the occlusion with it.
        float2 _ViewmodelTanHalf;

        float _AORadius;
        float _AOIntensity;
        float _AOBias;

        float4 _SpillColor;
        float _SpillRadius;
        float _SpillIntensity;
        float _SpillFalloff;

        float2 _BlurStep;

        #define SAMPLE_COUNT 12
        #define GOLDEN_ANGLE 2.39996323

        // Contrast on the finished occlusion, same constant and same purpose as
        // URP's kContrast: below 1 it lifts the midtones, so the AO reads as a
        // gradient rather than as a stamp.
        #define AO_CONTRAST 0.6

        // Cleared depth is 0 under reversed-Z -- which is every desktop target
        // Unity ships today -- and 1 otherwise. Getting this backwards makes the
        // whole screen read as weapon, which is not a subtle failure.
        bool IsWeapon(float rawDepth)
        {
        #if UNITY_REVERSED_Z
            return rawDepth > 0.0;
        #else
            return rawDepth < 1.0;
        #endif
        }

        // Clamped, because the sample discs below reach off-screen at the frame
        // edges and the wrap mode on a depth attachment is not ours to trust --
        // repeat there would fetch the far side of the screen and darken one
        // border with the other one's weapon.
        float SampleWeaponDepth(float2 uv)
        {
            return SAMPLE_TEXTURE2D_LOD(_ViewmodelDepthTex, sampler_ViewmodelDepthTex, saturate(uv), 0).r;
        }

        // View-space position of the surface seen at uv, at the viewmodel's own
        // field of view. Unity's view space looks down -Z, hence the sign.
        float3 ViewPos(float2 uv, float linearDepth)
        {
            float2 ndc = uv * 2.0 - 1.0;
            return float3(ndc.x * _ViewmodelTanHalf.x,
                          ndc.y * _ViewmodelTanHalf.y,
                          -1.0) * linearDepth;
        }

        // Falls back to the centre's depth off the silhouette, so a neighbour
        // that landed on empty screen contributes a surface at the same distance
        // instead of one at the far plane. Without it every edge pixel gets a
        // normal built from a cliff that is not there and darkens hard.
        float WeaponDepthOr(float2 uv, float fallback)
        {
            float raw = SampleWeaponDepth(uv);
            return IsWeapon(raw) ? LinearEyeDepth(raw, _ZBufferParams) : fallback;
        }

        // Depth-derived normal, four taps with the closer neighbour on each axis
        // chosen -- the same trick URP uses, and for the same reason: at a
        // silhouette one side of the pixel is on the surface and the other is
        // across a discontinuity, and picking the near one keeps the plane on
        // the surface it belongs to.
        float3 ReconstructNormal(float2 uv, float depthO, float3 vposO, float2 texel)
        {
            float2 lUV = uv + float2(-texel.x, 0.0);
            float2 rUV = uv + float2( texel.x, 0.0);
            float2 uUV = uv + float2(0.0,  texel.y);
            float2 dUV = uv + float2(0.0, -texel.y);

            float lD = WeaponDepthOr(lUV, depthO);
            float rD = WeaponDepthOr(rUV, depthO);
            float uD = WeaponDepthOr(uUV, depthO);
            float dD = WeaponDepthOr(dUV, depthO);

            bool takeLeft = abs(lD - depthO) < abs(rD - depthO);
            bool takeDown = abs(dD - depthO) < abs(uD - depthO);

            float3 P1;
            float3 P2;
            if (takeDown)
            {
                P1 = takeLeft ? ViewPos(lUV, lD) : ViewPos(dUV, dD);
                P2 = takeLeft ? ViewPos(dUV, dD) : ViewPos(rUV, rD);
            }
            else
            {
                P1 = takeLeft ? ViewPos(uUV, uD) : ViewPos(rUV, rD);
                P2 = takeLeft ? ViewPos(lUV, lD) : ViewPos(uUV, uD);
            }

            float3 n = normalize(cross(P2 - vposO, P1 - vposO));

            // A surface that can be seen faces the camera, so its view-space
            // normal points along +Z. Flipping the ones that came out backwards
            // is cheaper than getting the winding right for every branch above.
            return n.z < 0.0 ? -n : n;
        }

        // Interleaved gradient noise. Written out rather than included because
        // its home moves between core versions, and it is four lines.
        float InterleavedNoise(float2 pixel)
        {
            return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
        }
        ENDHLSL

        // ── Pass 0: occlusion ───────────────────────────────────────────
        // Half resolution, RG. One spiral of directions, sampled twice per
        // step: once at the AO radius against the weapon's own surface, once at
        // the spill radius against the silhouette.
        Pass
        {
            Name "ViewmodelAOCompute"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float2 texel = _ViewmodelScreenSize.zw;

                float rawO = SampleWeaponDepth(uv);
                bool isWeapon = IsWeapon(rawO);

                // Every pixel gets its own rotation and its own set of radii.
                // This is the part that makes twelve samples enough: without it
                // neighbouring pixels ask identical questions, get identical
                // answers, and the blur has nothing to average.
                float noise = InterleavedNoise(uv * _ViewmodelScreenSize.xy);

                float depthO = 0.0;
                float3 vposO = 0.0;
                float3 normalO = 0.0;
                float worldRadius = 0.0;

                if (isWeapon)
                {
                    depthO = LinearEyeDepth(rawO, _ZBufferParams);
                    vposO = ViewPos(uv, depthO);

                    // Two texels, because this reads a full-resolution depth
                    // from a half-resolution pass -- one texel here is the same
                    // step the sampling grid takes.
                    normalO = ReconstructNormal(uv, depthO, vposO, texel * 2.0);

                    // The AO radius is authored in pixels, so the world distance
                    // it stands for depends on how far away the surface is. This
                    // converts it once, and the range test below then matches the
                    // sampling extent for free instead of needing a knob of its
                    // own.
                    worldRadius = _AORadius * texel.y * 2.0 * _ViewmodelTanHalf.y * depthO;
                }

                const float rcpCount = rcp((float)SAMPLE_COUNT);
                float occlusion = 0.0;
                float coverage = 0.0;

                UNITY_UNROLL
                for (int s = 0; s < SAMPLE_COUNT; s++)
                {
                    float theta = s * GOLDEN_ANGLE + noise * 6.28318531;

                    // Square-rooted so the points spread evenly over the disc
                    // rather than piling up in the middle, and offset by the
                    // noise so the radii differ pixel to pixel as well as the
                    // angles.
                    float radius = sqrt((s + noise) * rcpCount);

                    float2 dir = float2(cos(theta), sin(theta)) * radius;

                    // -- silhouette coverage --
                    float2 uvSpill = uv + dir * _SpillRadius * texel;
                    coverage += IsWeapon(SampleWeaponDepth(uvSpill)) ? 1.0 : 0.0;

                    // -- the weapon's own occlusion --
                    if (isWeapon)
                    {
                        float2 uvAO = uv + dir * _AORadius * texel;
                        float rawS = SampleWeaponDepth(uvAO);
                        if (!IsWeapon(rawS))
                            continue;

                        float depthS = LinearEyeDepth(rawS, _ZBufferParams);

                        // Measured in units of the sampling radius, not in metres.
                        //
                        // URP's constants are sized for its own radius of 0.3 m.
                        // This radius is authored in pixels, and a weapon held 30 cm
                        // from the lens turns five pixels into about a millimetre and
                        // a half of world space -- two hundred times smaller. Left in
                        // metres the self-shadow bias alone (0.01 x 0.3 m = 3 mm) was
                        // wider than the entire sampling disc, so the numerator was
                        // negative at every sample and the occlusion came out exactly
                        // zero. Dividing through by the radius first makes every
                        // constant below a fraction, which is the same number at any
                        // radius and any distance the weapon is held at.
                        float3 vn = (ViewPos(uvAO, depthS) - vposO) * rcp(worldRadius);
                        float vLenSq = dot(vn, vn);

                        // Alchemy's obscurance estimator (Morgan 2011), the same one
                        // URP uses -- and, once both sides are divided by the radius,
                        // algebraically the same expression. The bias subtracted from
                        // the numerator is what stops a flat surface shadowing itself
                        // out of its own depth precision; the epsilon in the
                        // denominator is what stops a sample landing almost on top of
                        // the centre from dividing by nothing. 0.001 is URP's own
                        // 0.0001, expressed as the fraction of its radius it works out
                        // to there.
                        float a1 = max(dot(vn, normalO) - _AOBias, 0.0);
                        float a2 = vLenSq + 0.001;
                        float inRange = vLenSq < 1.0 ? 1.0 : 0.0;

                        occlusion += a1 * rcp(a2) * inRange;
                    }
                }

                coverage *= rcpCount;

                // No radius factor here: the samples were divided by it going in,
                // which is what URP's "ao *= RADIUS" does on the way out. Then
                // contrast-curved the way URP curves it.
                occlusion = pow(saturate(occlusion * _AOIntensity * rcpCount), AO_CONTRAST);

                return float4(occlusion, coverage, 0.0, 0.0);
            }
            ENDHLSL
        }

        // ── Pass 1: separable blur ──────────────────────────────────────
        // Not geometry-aware, unlike URP's. URP's bilateral weighting exists to
        // stop occlusion leaking across a silhouette; here the leak IS half the
        // effect -- the spill is supposed to cross from the weapon into the room
        // -- so the plain average is the correct one.
        Pass
        {
            Name "ViewmodelAOBlur"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                // Five taps at Unity's own spacing and weights: a nine-tap
                // Gaussian folded onto bilinear fetches, so the offsets are
                // fractions of a texel rather than whole ones.
                float2 d1 = _BlurStep * 1.3846153846;
                float2 d2 = _BlurStep * 3.2307692308;

                float2 s = SAMPLE_TEXTURE2D(_ViewmodelAOTex, sampler_ViewmodelAOTex, uv).rg * 0.2270270270;
                s += SAMPLE_TEXTURE2D(_ViewmodelAOTex, sampler_ViewmodelAOTex, uv - d1).rg * 0.3162162162;
                s += SAMPLE_TEXTURE2D(_ViewmodelAOTex, sampler_ViewmodelAOTex, uv + d1).rg * 0.3162162162;
                s += SAMPLE_TEXTURE2D(_ViewmodelAOTex, sampler_ViewmodelAOTex, uv - d2).rg * 0.0702702703;
                s += SAMPLE_TEXTURE2D(_ViewmodelAOTex, sampler_ViewmodelAOTex, uv + d2).rg * 0.0702702703;

                return float4(s, 0.0, 0.0);
            }
            ENDHLSL
        }

        // ── Pass 2: composite ───────────────────────────────────────────
        Pass
        {
            Name "ViewmodelAOComposite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float4 col = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv);

                // Bilinear on purpose: this is the one place the half-size
                // buffer is read back at full size, so the hardware's
                // interpolation does the last of the smoothing for nothing.
                float2 ao = SAMPLE_TEXTURE2D(_ViewmodelAOTex, sampler_ViewmodelAOTex, uv).rg;

                // Coverage is 1 deep inside the weapon and 0 well outside it, so
                // it passes through 0.5 exactly at the silhouette. c*(1-c) peaks
                // there and vanishes at both ends, and the 4 scales that peak to
                // 1 -- a band that reaches as far inward onto the weapon as it
                // does outward into the room.
                float band = saturate(4.0 * ao.g * (1.0 - ao.g));
                band = pow(band, max(_SpillFalloff, 0.01));

                float spill = saturate(band * _SpillIntensity) * _SpillColor.a;

                // Occlusion multiplies, the way light being taken away does.
                // The spill is a tint, so it lerps -- it has a colour of its own.
                float3 rgb = col.rgb * (1.0 - saturate(ao.r));
                rgb = lerp(rgb, _SpillColor.rgb, spill);

                return float4(rgb, col.a);
            }
            ENDHLSL
        }
    }
}

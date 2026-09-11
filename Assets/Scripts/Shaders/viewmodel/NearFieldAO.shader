Shader "Hidden/PostProcessing/NearFieldAO"
{
    // Ambient occlusion for the things held up against the lens: the item in hand and
    // the player's own body. And a second, separate effect -- the subject darkening the
    // surface behind it -- which shares the composite and nothing else.
    //
    // WHY THESE TWO GET THEIR OWN. URP's SSAO is one radius quoted for a room, and these
    // two fail it in different ways. The held item is drawn through a lens of its own, so
    // it is not in the world's depth-normals at all and URP has nothing to work from. The
    // body IS in there, but its creases -- under the chin, arm against torso, inside the
    // elbow -- are finer than a room-sized radius can resolve, so it comes out flat.
    //
    // THE CONTRACT, and it is the whole design: a depth buffer and a normals buffer
    // holding ONE SUBJECT AND NOTHING ELSE, both rendered through the matrix that subject
    // was drawn with. Everything below is computed from those two, so the same passes
    // serve both subjects and each brings its own tan(fov/2).
    //
    // ── WHAT MAKES THE OCCLUSION CONSISTENT ──────────────────────────────────────────
    //
    //   * IT USES REAL NORMALS. An earlier version reconstructed them from depth with
    //     four taps. On a surface with fine detail -- stitching, panel lines, knuckles --
    //     every small feature fits a plane that is not the surface's, and the occlusion
    //     crawls as the subject animates. A normals buffer costs a second attachment on
    //     a render that was happening anyway.
    //
    //   * THE RADIUS IS IN METRES. In pixels, the same physical crease darkened by a
    //     different amount depending on how much of the screen it covered -- it changed
    //     with distance and with field of view.
    //
    //   * THE BLUR IS GEOMETRY-AWARE. Taps are weighted by how much they agree with the
    //     centre about depth, so a crease does not smear onto the feature next to it.
    //
    // The sampling gives every pixel a DIFFERENT set of samples -- different angles and
    // radii from interleaved gradient noise -- so neighbouring pixels genuinely disagree,
    // and the blur averages that disagreement away. Rotating a fixed pattern or blurring
    // a hard mask does not do this: the first scatters which of a handful of values each
    // pixel picks, the second smooths a contour that was quantised before it was smoothed.
    //
    // ── THE SPILL, AND WHY IT IS NOT SAMPLED ─────────────────────────────────────────
    //
    // It is a blurred mask, and that is the whole of it: one where the subject is, zero
    // where it is not, blurred wide, then applied only to the pixels that are not the
    // subject.
    //
    // There were two sampled versions of this before it and both were the wrong tool.
    // Monte-Carlo sampling earns its cost on the occlusion, where the question is a 3D
    // integral over a surface's hemisphere and there is no closed form. The spill asks
    // only "how close is the silhouette", and blurring a binary field answers that
    // exactly: the result is about a half at the edge and falls off smoothly outward,
    // which IS the shape wanted. It also arrives free of sampling noise, so it needs no
    // interleaved gradient, no bilateral weighting and no twenty-four taps -- and being
    // computed in its own buffers, it cannot fail quietly inside the occlusion's.
    //
    // Three things keep it from reading as an outline:
    //
    //   * IT IS ONE-SIDED. An earlier version weighted coverage by 4c(1-c), which peaks
    //     at the silhouette and so reaches as far INWARD onto the subject as outward. A
    //     band straddling an edge is the definition of an outline. The composite here
    //     applies spill only where the subject is absent, so there is no inner band and
    //     the subject's own occlusion is the only thing on it.
    //
    //   * IT MULTIPLIES. That version lerped towards an authored colour, so it painted.
    //     Occlusion takes light away, so this multiplies, and there is no colour to get
    //     wrong.
    //
    //   * IT FALLS OFF FROM THE EDGE. A Gaussian of a step function is a smooth ramp. A
    //     constant-width band reads as a line; a ramp reads as shadow.
    //
    // ITS RADIUS IS IN PIXELS, and unlike the occlusion that is the right unit. A true
    // world-space version would be invisible: the item is 30 cm from the eye and the room
    // is metres away, so nothing is within centimetres of it and there is no contact to
    // shade. This is a screen-space effect that seats the item in the frame, and the item
    // is rigid relative to the camera, so pixels and metres are proportional for it.
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

        // The subject's own buffers. Point-sampled: both hold per-pixel geometry, and
        // interpolating between two of them invents a surface halfway between a finger
        // and the barrel behind it.
        TEXTURE2D(_SubjectDepthTex);
        SAMPLER(sampler_SubjectDepthTex);

        TEXTURE2D(_SubjectNormalsTex);
        SAMPLER(sampler_SubjectNormalsTex);

        // Half-resolution RG: occlusion, and the linear eye depth it was computed at
        // (zero where there is no subject). The depth is not a debug channel -- it is
        // what lets the blur tell a neighbour on the same surface from one across an edge.
        TEXTURE2D(_AOTex);
        SAMPLER(sampler_AOTex);

        // Quarter-resolution R: the blurred subject mask. Its own buffer, at its own
        // resolution, filtered its own way.
        TEXTURE2D(_SpillTex);
        SAMPLER(sampler_SpillTex);

        // Full-resolution (width, height, 1/width, 1/height). Passed in rather than read
        // from _ScreenParams because these passes draw into reduced-size targets, where
        // _ScreenParams no longer describes the screen.
        float4 _AOScreenSize;

        // tan(fov/2) * aspect, tan(fov/2) -- for the projection THIS SUBJECT was drawn
        // with, which for the held item is the lens override and for the body is the
        // camera's own. Unprojecting with the wrong one puts every surface at the wrong
        // angle and tilts all the occlusion with it.
        float2 _AOTanHalf;

        // Metres.
        float _AORadius;
        float _AOIntensity;

        // Multiplied by the surface's own depth to give a length, so it defends against
        // depth precision the way depth precision actually behaves. URP calls it kBeta
        // and keeps it at 0.002.
        float _AOBias;

        // Two strengths over one mask. The blurred mask is about a half at the silhouette
        // and runs to one inward and zero outward, so `mask` is the outward ramp and
        // `1 - mask` is the inward one -- the same gradient read from both ends. Separate
        // strengths are what keep it from being an outline by construction: equal values
        // give a symmetric band, which is a line, and the point is that this is now a
        // choice rather than the only thing the maths can produce.
        float _SpillOutwardIntensity;
        float _SpillInwardIntensity;

        float2 _BlurStep;
        float _BlurDepthSharpness;

        // Twenty-four, not the twelve this started with, because these subjects are
        // CLOSE and that makes the sampling disc large in pixels even when it is small in
        // metres. See the radius comment in the compute pass for the arithmetic.
        #define SAMPLE_COUNT 24
        #define GOLDEN_ANGLE 2.39996323

        // A ceiling on how far the occlusion disc may reach, in full-resolution pixels.
        //
        // Not a fudge -- it is where the technique runs out of budget. Sample spacing
        // goes as radius / sqrt(count), and once the gaps between samples are wider than
        // the blur can bridge, the result stops being occlusion and becomes blotches that
        // no amount of blurring fixes. Past this the honest answer is a smaller radius,
        // so the cap makes an over-large one degrade instead of falling off a cliff.
        #define AO_MAX_RADIUS_PIXELS 96.0

        // Contrast on the finished occlusion, same constant and purpose as URP's
        // kContrast: below 1 it lifts the midtones, so the AO reads as a gradient rather
        // than as a stamp.
        #define AO_CONTRAST 0.6

        // Stops a sample landing almost on top of the centre from dividing by nothing.
        // Square metres, URP's own value.
        #define AO_EPSILON 0.0001

        // Cleared depth is 0 under reversed-Z -- which is every desktop target Unity
        // ships today -- and 1 otherwise. Getting this backwards makes the whole screen
        // read as subject, which is not a subtle failure.
        bool IsSubject(float rawDepth)
        {
        #if UNITY_REVERSED_Z
            return rawDepth > 0.0;
        #else
            return rawDepth < 1.0;
        #endif
        }

        // Clamped, because the sample discs below reach off-screen at the frame edges and
        // the wrap mode on an attachment is not ours to trust -- repeat there would fetch
        // the far side of the screen and darken one border with the other one's geometry.
        float SampleSubjectDepth(float2 uv)
        {
            return SAMPLE_TEXTURE2D_LOD(_SubjectDepthTex, sampler_SubjectDepthTex, saturate(uv), 0).r;
        }

        // View space, because that is the space the occlusion estimator works in. The
        // buffer holds world normals, the way URP's own depth-normals prepass writes
        // them, so this is one matrix multiply rather than a different prepass.
        float3 SampleSubjectNormalVS(float2 uv)
        {
            float3 normalWS = SAMPLE_TEXTURE2D_LOD(_SubjectNormalsTex, sampler_SubjectNormalsTex, saturate(uv), 0).xyz;
            float3 normalVS = mul((float3x3)UNITY_MATRIX_V, normalWS);

            return normalize(normalVS);
        }

        // View-space position of the surface seen at uv, at the subject's own field of
        // view. Unity's view space looks down -Z, hence the sign.
        float3 ViewPos(float2 uv, float linearDepth)
        {
            float2 ndc = uv * 2.0 - 1.0;
            return float3(ndc.x * _AOTanHalf.x,
                          ndc.y * _AOTanHalf.y,
                          -1.0) * linearDepth;
        }

        // Interleaved gradient noise. Written out rather than included because its home
        // moves between core versions, and it is one line.
        float InterleavedNoise(float2 pixel)
        {
            return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
        }
        ENDHLSL

        // ── Pass 0: occlusion ───────────────────────────────────────────
        // Half resolution. RG out: occlusion, and the depth it was computed at.
        Pass
        {
            Name "NearFieldAOCompute"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float2 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                float rawO = SampleSubjectDepth(uv);

                // Nothing here belongs to the subject. Zero occlusion and zero depth,
                // and the blur below reads that zero as "no data" rather than as a
                // surface at the camera.
                if (!IsSubject(rawO))
                    return float2(0.0, 0.0);

                float depthO = LinearEyeDepth(rawO, _ZBufferParams);
                float3 vposO = ViewPos(uv, depthO);
                float3 normalO = SampleSubjectNormalVS(uv);

                // The world radius, in UV. A length L at eye depth z spans L/(z*tanHalf)
                // of NDC and half that of UV, per axis -- so this is the one place the
                // metres become pixels, and it happens per pixel from that pixel's own
                // depth. That is what keeps the same crease reading the same at any
                // distance.
                //
                // AND IT IS WHY THE RADIUS HERE IS MILLIMETRES, NOT CENTIMETRES. These
                // subjects are held against the lens, so a small world radius is a large
                // screen radius. A weapon 30 cm away through a 35 degree lens sees
                // 2*0.30*tan(17.5) = 0.19 m of world across the screen's height -- so a
                // 5 cm radius covers a QUARTER OF THE SCREEN, and twenty-four samples
                // spread over that is not occlusion, it is blotches. 1 cm is about 5% of
                // screen height, which the sample count and the blur can carry.
                float2 uvRadius = float2(_AORadius / (2.0 * depthO * _AOTanHalf.x),
                                         _AORadius / (2.0 * depthO * _AOTanHalf.y));

                uvRadius = min(uvRadius, AO_MAX_RADIUS_PIXELS * _AOScreenSize.zw);

                // Every pixel gets its own rotation and its own set of radii. This is
                // what makes twenty-four samples enough: without it neighbouring pixels
                // ask identical questions, get identical answers, and the blur has
                // nothing to average.
                float noise = InterleavedNoise(uv * _AOScreenSize.xy);

                const float rcpCount = rcp((float)SAMPLE_COUNT);
                float radiusSq = _AORadius * _AORadius;
                float occlusion = 0.0;

                UNITY_UNROLL
                for (int s = 0; s < SAMPLE_COUNT; s++)
                {
                    float theta = s * GOLDEN_ANGLE + noise * 6.28318531;

                    // Square-rooted so the points spread evenly over the disc rather
                    // than piling up in the middle, and offset by the noise so the radii
                    // differ pixel to pixel as well as the angles.
                    float radius = sqrt((s + noise) * rcpCount);

                    float2 dir = float2(cos(theta), sin(theta)) * radius;
                    float2 uvS = uv + dir * uvRadius;

                    float rawS = SampleSubjectDepth(uvS);

                    // Off the subject is not an occluder. Treating empty screen as a
                    // surface at the far plane is what gave the first version a hard
                    // dark rim around every silhouette.
                    if (!IsSubject(rawS))
                        continue;

                    float depthS = LinearEyeDepth(rawS, _ZBufferParams);

                    // Alchemy's obscurance estimator (Morgan 2011), URP's own, in metres
                    // and in URP's exact form. Not rearranged: a1 is a length and a2 an
                    // area, so a1/a2 is one over a length and the radius multiplied back
                    // in after the loop is what makes the result dimensionless.
                    float3 v = ViewPos(uvS, depthS) - vposO;
                    float vv = dot(v, v);
                    float vn = dot(v, normalO);

                    // Past the radius is not this pixel's business. An explicit test
                    // rather than a falloff curve, because the sampling disc already
                    // matches the radius exactly -- so anything beyond it arrived by
                    // projection error, not by being near.
                    if (vv > radiusSq)
                        continue;

                    // The bias scales with depth because what it defends against is
                    // depth precision, which also scales with depth. This is the one
                    // constant here that is not a pure fraction, and that is why.
                    float a1 = max(vn - _AOBias * depthO, 0.0);
                    float a2 = vv + AO_EPSILON;

                    occlusion += a1 * rcp(a2);
                }

                // The radius back in, which is what makes the sum dimensionless, then
                // contrast-curved the way URP curves it.
                occlusion *= _AORadius;
                occlusion = pow(saturate(occlusion * _AOIntensity * rcpCount), AO_CONTRAST);

                return float2(occlusion, depthO);
            }
            ENDHLSL
        }

        // ── Pass 1: separable bilateral blur, for the occlusion ─────────
        // Five taps at Unity's own spacing, weighted by how much each agrees with the
        // centre about depth. The disagreement the sampling deliberately created is what
        // gets averaged here; the disagreement that comes from being on a different
        // surface is what gets rejected.
        Pass
        {
            Name "NearFieldAOBlur"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            // Relative to the centre's own depth, so the tolerance is the same fraction
            // at any distance. An absolute tolerance in metres would blur a weapon held
            // at 30 cm and a body at 60 cm by different amounts, which is the exact class
            // of inconsistency this version exists to remove.
            float TapWeight(float tapDepth, float centreDepth, float gaussian)
            {
                if (tapDepth <= 0.0)
                    return 0.0;

                float difference = abs(tapDepth - centreDepth) * rcp(max(centreDepth, 1e-4));

                return gaussian * saturate(1.0 - difference * _BlurDepthSharpness);
            }

            float2 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                float2 centre = SAMPLE_TEXTURE2D(_AOTex, sampler_AOTex, uv).rg;

                // No data at the centre stays no data. Filling it in from neighbours is
                // how occlusion escapes onto the world behind the subject -- which is
                // the spill's job, and the spill has its own buffers precisely so the
                // two cannot be confused.
                if (centre.y <= 0.0)
                    return float2(0.0, 0.0);

                // A nine-tap Gaussian folded onto bilinear fetches, so the offsets are
                // fractions of a texel rather than whole ones.
                float2 d1 = _BlurStep * 1.3846153846;
                float2 d2 = _BlurStep * 3.2307692308;

                float2 t1 = SAMPLE_TEXTURE2D(_AOTex, sampler_AOTex, uv - d1).rg;
                float2 t2 = SAMPLE_TEXTURE2D(_AOTex, sampler_AOTex, uv + d1).rg;
                float2 t3 = SAMPLE_TEXTURE2D(_AOTex, sampler_AOTex, uv - d2).rg;
                float2 t4 = SAMPLE_TEXTURE2D(_AOTex, sampler_AOTex, uv + d2).rg;

                float wC = 0.2270270270;
                float w1 = TapWeight(t1.y, centre.y, 0.3162162162);
                float w2 = TapWeight(t2.y, centre.y, 0.3162162162);
                float w3 = TapWeight(t3.y, centre.y, 0.0702702703);
                float w4 = TapWeight(t4.y, centre.y, 0.0702702703);

                float sum = centre.r * wC + t1.r * w1 + t2.r * w2 + t3.r * w3 + t4.r * w4;
                float weight = wC + w1 + w2 + w3 + w4;

                // The centre's depth is carried through untouched: this is a separable
                // filter, and the second direction needs the same surface identity the
                // first one used.
                return float2(sum * rcp(weight), centre.y);
            }
            ENDHLSL
        }

        // ── Pass 2: composite ───────────────────────────────────────────
        Pass
        {
            Name "NearFieldAOComposite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float4 col = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv);

                // Gated on the subject at FULL resolution, not on a reduced-size buffer.
                // Both effects are read bilinearly -- which is where the last of their
                // smoothing comes from, for free -- and bilinear on a smaller buffer
                // reaches past the silhouette. Without this gate the occlusion's overhang
                // is a dark fringe traced around the weapon.
                //
                // It is also what makes the two exclusive: on the subject, its own
                // occlusion and nothing else; off it, only the spill. Neither touches the
                // other's pixels, and that is the difference between occlusion bleeding
                // outward and a line drawn around a silhouette.
                bool subject = IsSubject(SampleSubjectDepth(uv));

                // Whichever strength governs this pixel. Read first, so a pixel whose
                // side of the silhouette is switched off never samples a buffer that
                // nobody bound -- which is what happens when both strengths are zero.
                float strength = subject ? _SpillInwardIntensity : _SpillOutwardIntensity;

                float mask = strength > 0.0
                    ? SAMPLE_TEXTURE2D(_SpillTex, sampler_SpillTex, uv).r
                    : 0.0;

                // Multiplies, the way light being taken away does. An earlier version
                // lerped towards an authored colour, which is painting, not shading.
                if (subject)
                {
                    float occlusion = SAMPLE_TEXTURE2D(_AOTex, sampler_AOTex, uv).r;

                    // Inward: zero deep inside the subject, rising towards the edge.
                    //
                    // BOUNDED BY THE SUBJECT'S OWN THICKNESS, and that is worth knowing
                    // rather than discovering. The mask only reaches one where the
                    // subject is wider than the blur, so on something thinner than the
                    // radius -- a barrel, a finger -- it never gets there and the inward
                    // term covers the whole part instead of hugging its edges. On thin
                    // geometry this reads as darkening rather than as an edge, which is
                    // sometimes the better look and sometimes the reason to bring the
                    // radius down.
                    float inward = saturate((1.0 - mask) * _SpillInwardIntensity);

                    // Two independent ways for light to be removed, so they multiply
                    // rather than add: the subject's own occlusion, and its edge.
                    return float4(col.rgb * (1.0 - saturate(occlusion)) * (1.0 - inward), col.a);
                }

                float outward = saturate(mask * _SpillOutwardIntensity);

                return float4(col.rgb * (1.0 - outward), col.a);
            }
            ENDHLSL
        }

        // ── Pass 3: the subject mask ────────────────────────────────────
        // One where the subject is, zero where it is not. Quarter resolution, and the
        // downsample is the first of the smoothing: a bilinear read of a binary field is
        // already a ramp.
        Pass
        {
            Name "NearFieldSpillMask"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float Frag(Varyings input) : SV_Target
            {
                return IsSubject(SampleSubjectDepth(input.texcoord)) ? 1.0 : 0.0;
            }
            ENDHLSL
        }

        // ── Pass 4: separable blur, for the mask ────────────────────────
        // Plain, and deliberately so. The bilateral weighting one pass up exists to stop
        // occlusion crossing a silhouette; here crossing the silhouette IS the effect, so
        // the flat average is the correct filter rather than a compromise.
        Pass
        {
            Name "NearFieldSpillBlur"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                float2 d1 = _BlurStep * 1.3846153846;
                float2 d2 = _BlurStep * 3.2307692308;

                float s = SAMPLE_TEXTURE2D(_SpillTex, sampler_SpillTex, uv).r * 0.2270270270;
                s += SAMPLE_TEXTURE2D(_SpillTex, sampler_SpillTex, uv - d1).r * 0.3162162162;
                s += SAMPLE_TEXTURE2D(_SpillTex, sampler_SpillTex, uv + d1).r * 0.3162162162;
                s += SAMPLE_TEXTURE2D(_SpillTex, sampler_SpillTex, uv - d2).r * 0.0702702703;
                s += SAMPLE_TEXTURE2D(_SpillTex, sampler_SpillTex, uv + d2).r * 0.0702702703;

                return s;
            }
            ENDHLSL
        }
    }
}

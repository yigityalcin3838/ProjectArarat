Shader "Hidden/PostProcessing/NearFieldDepthOfField"
{
    // Depth of field that knows where the held item actually is.
    //
    // URP's own was switched off for one reason: the item is drawn through a lens of its
    // own, so its pixels are nowhere near where _CameraDepthTexture says they are. Any
    // effect that reads depth and then samples neighbouring pixels smears the
    // background's blur across the weapon's silhouette, or punches a hole in it. That
    // was never a bug in URP's effect -- it was two projections arguing over one depth
    // buffer, and no parameter fixes it.
    //
    // So this one starts by building a depth buffer that both projections agree on, and
    // everything after that is ordinary.
    //
    // ── AUTOFOCUS ────────────────────────────────────────────────────────────────────
    //
    // Measured off the merged depth buffer, which is to say off WHAT WAS ACTUALLY DRAWN.
    // There was a version of this that cast a physics ray down the aim direction, and it
    // disagreed with the picture in every case that mattered: it passed through anything
    // without a collider, it stopped on triggers and invisible blockers, and it found the
    // player's own capsule. The depth buffer has no such opinions -- if a pixel is on
    // screen, something drew it, and its distance is right there.
    //
    // The measurement and its smoothing both live on the GPU, in a one-pixel texture
    // ping-ponged between frames. Reading a depth value back to the CPU to smooth it
    // there would mean either a pipeline stall or a frame of latency, and neither is
    // worth it for a number that only has to ease.
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

        // Raw device depth of whatever the lens pass drew, and nothing else -- that pass
        // clears depth and draws only the held item, so every other pixel is at the
        // cleared value. Point-sampled: a depth value interpolated with its neighbour
        // describes a surface halfway between a barrel and the wall behind it.
        TEXTURE2D(_ForegroundDepthTex);
        SAMPLER(sampler_ForegroundDepthTex);

        // The result of the merge: raw device depth, foreground where there is
        // foreground, the world everywhere else.
        TEXTURE2D(_MergedDepthTex);
        SAMPLER(sampler_MergedDepthTex);

        // One pixel. Holds last frame's focus distance, in metres.
        TEXTURE2D(_PrevFocusTex);
        SAMPLER(sampler_PrevFocusTex);

        // One pixel. Holds this frame's, after smoothing.
        TEXTURE2D(_FocusTex);
        SAMPLER(sampler_FocusTex);

        // Half-resolution: rgb is colour, a is the signed circle of confusion in
        // half-resolution pixels. Negative in front of the focal plane, positive behind.
        TEXTURE2D(_DofColorTex);
        SAMPLER(sampler_DofColorTex);

        TEXTURE2D(_DofBlurredTex);
        SAMPLER(sampler_DofBlurredTex);

        float4 _DofScreenSize;

        // Where on screen autofocus looks, in UV, and how wide a disc it searches.
        float2 _FocusPoint;
        float _FocusSearchRadius;

        // 0..1 per frame, already converted from a rate and a delta time on the CPU so
        // the shader does not need either.
        float _FocusLerp;

        // Autofocus only engages on surfaces nearer than this.
        //
        // PAST IT THE EFFECT SWITCHES ITSELF OFF, rather than focusing somewhere else.
        // There was a resting distance here for that job and it was the wrong answer:
        // parking focus at sixty metres does make the world sharp, but it leaves the item
        // 30 cm from the eye violently blurred, and "nothing near me" is supposed to mean
        // a clean frame. So the focus texture carries a second number -- how engaged the
        // effect is -- and everything scales by it.
        //
        // Which also settles a question the resting distance could not. Disengaged, the
        // DISTANCE is held rather than eased anywhere, so walking back into a corridor
        // picks up where it left off instead of sweeping in from sixty metres while the
        // blur fades up underneath it.
        float _AutofocusMaxDistance;

        // How much of the foreground's FOCUS-DERIVED blur to show. Its own multiplier and
        // not part of the circle of confusion, because it answers a different question:
        // the item is violently out of focus whenever the world is in focus, and how much
        // of that to put on screen is a look decision rather than a consequence.
        float _ForegroundBlurScale;

        // A blur the foreground carries WHATEVER FOCUS IS DOING, as a fraction of the
        // maximum radius.
        //
        // This is not depth of field and it is deliberately not wired to any of it. What
        // the walk offset asks for -- the thing in the player's hands softening as it is
        // carried -- is not a statement about a focal plane, and trying to express it as
        // one fell apart: out in the open nothing is within the autofocus gate, the effect
        // stands down so the world reads clean, and a foreground blur that depended on
        // that engagement went with it. So it does not depend on it. The floor is
        // independent of focus, of engagement, and of the scale above, and the foreground
        // simply takes whichever of the two is larger.
        float _ForegroundBlurFloor;

        // Metres of distance either side of the focal plane that stay sharp, and metres
        // over which the blur then ramps to its maximum.
        float _FocusRange;
        float _BlurFalloff;

        // Full-resolution pixels.
        float _MaxBlurRadius;

        #define GATHER_COUNT 24
        #define GOLDEN_ANGLE 2.39996323

        // Cleared depth is 0 under reversed-Z -- which is every desktop target Unity
        // ships today -- and 1 otherwise.
        //
        // GETTING THIS BACKWARDS DOES NOT LOOK LIKE A DEPTH BUG. On reversed-Z, testing
        // for "< 1" is true almost everywhere, so the whole screen reads as foreground,
        // the merged buffer comes out at the far plane for every pixel, and a depth of
        // field with no depth in it blurs nothing at all. The effect appears to be
        // switched off. That happened once already, in the version of this merge that
        // shipped before, and the comment is here so it does not happen twice.
        bool IsForeground(float rawDepth)
        {
        #if UNITY_REVERSED_Z
            return rawDepth > 0.0;
        #else
            return rawDepth < 1.0;
        #endif
        }

        float MergedEyeDepth(float2 uv)
        {
            float raw = SAMPLE_TEXTURE2D_LOD(_MergedDepthTex, sampler_MergedDepthTex, saturate(uv), 0).r;

            return LinearEyeDepth(raw, _ZBufferParams);
        }

        // Signed circle of confusion, in full-resolution pixels.
        //
        // An artistic model rather than a thin lens, because the knobs a thin lens gives
        // you -- aperture, focal length, sensor size -- are three numbers that interact,
        // and what is actually wanted here is "sharp within this much, blurred by that
        // much". Near and far use the same falloff on purpose: two of them is a fourth
        // knob for a difference nobody looking at the frame can name.
        float CircleOfConfusion(float eyeDepth, float focus)
        {
            float signedDistance = eyeDepth - focus;
            float beyondRange = max(abs(signedDistance) - _FocusRange, 0.0);
            float ramp = saturate(beyondRange * rcp(max(_BlurFalloff, 1e-4)));

            return sign(signedDistance) * ramp * _MaxBlurRadius;
        }

        // The same thing, with the foreground's exemption applied. Every pass that needs
        // a circle of confusion goes through here rather than through the function above,
        // so the gather and the composite cannot disagree about how blurred the held item
        // is -- and disagreeing would show as the item's own silhouette being composited
        // from two different images.
        float CircleOfConfusionAt(float2 uv, float2 focus)
        {
            float coc = CircleOfConfusion(MergedEyeDepth(uv), focus.x);

            bool foreground = IsForeground(
                SAMPLE_TEXTURE2D_LOD(_ForegroundDepthTex, sampler_ForegroundDepthTex, saturate(uv), 0).r);

            if (foreground)
            {
                // Whichever is larger, not the sum: the floor is a minimum softness, not
                // an addition to whatever focus was already doing. Added, a weapon out of
                // focus in a corridor would blur twice as hard for walking through it.
                float focused = abs(coc) * _ForegroundBlurScale * focus.y;
                float floorBlur = _ForegroundBlurFloor * _MaxBlurRadius;

                // Negative, because the foreground is nearer than the focal plane in every
                // case this can arise -- it is bolted to the camera. The gather works in
                // absolute radii, so the sign only has to be honest, not load-bearing.
                return -max(focused, floorBlur);
            }

            // Engagement over the world, and only the world. It is not a focus parameter
            // -- it is whether there is an effect at all -- and out of range it takes the
            // whole thing to nothing, which is what leaves an open field reading clean.
            return coc * focus.y;
        }

        // Distance and engagement, from the one pixel that holds both.
        float2 SampleFocus()
        {
            return SAMPLE_TEXTURE2D(_FocusTex, sampler_FocusTex, float2(0.5, 0.5)).rg;
        }
        ENDHLSL

        // ── Pass 0: merge ───────────────────────────────────────────────
        // _BlitTexture is the camera's depth texture; _ForegroundDepthTex is the held
        // item's. Foreground wins where there is foreground -- not a depth comparison,
        // because the two were drawn through different projections and comparing their
        // depths is the very thing that does not work. The item is in front by
        // construction: it is bolted to the camera.
        Pass
        {
            Name "NearFieldDepthMerge"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                float worldDepth = SAMPLE_TEXTURE2D(_BlitTexture, sampler_PointClamp, uv).r;
                float foregroundDepth = SAMPLE_TEXTURE2D(_ForegroundDepthTex, sampler_ForegroundDepthTex, uv).r;

                return IsForeground(foregroundDepth) ? foregroundDepth : worldDepth;
            }
            ENDHLSL
        }

        // ── Pass 1: autofocus ───────────────────────────────────────────
        // One pixel out. Finds the nearest surface in a small disc around the focus
        // point, then eases last frame's answer towards it.
        Pass
        {
            Name "NearFieldDepthOfFieldFocus"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float2 Frag(Varyings input) : SV_Target
            {
                // THE NEAREST, not the average. Averaging across a silhouette returns a
                // distance where nothing is -- put a railing in front of a mountain and
                // the lens focuses on the gap between them. The nearest tap is always a
                // real surface, which is also what a camera's autofocus settles on.
                float nearest = MergedEyeDepth(_FocusPoint);

                UNITY_UNROLL
                for (int s = 0; s < 8; s++)
                {
                    float theta = s * GOLDEN_ANGLE;
                    float2 dir = float2(cos(theta), sin(theta)) * sqrt((s + 0.5) * 0.125);

                    float2 uv = _FocusPoint + dir * _FocusSearchRadius * _DofScreenSize.zw;

                    nearest = min(nearest, MergedEyeDepth(uv));
                }

                // THE GATE. Nothing within range means there is nothing to focus ON, so
                // the effect stands down -- rather than focusing on the mountain at the
                // far end of the valley, which would blur the whole foreground for no
                // reason anybody looking at the frame could name.
                bool engaged = nearest <= _AutofocusMaxDistance;

                float2 previous = SAMPLE_TEXTURE2D(_PrevFocusTex, sampler_PrevFocusTex, float2(0.5, 0.5)).rg;

                // First frame, or after a resize: the history is not a distance. Land on
                // the measurement rather than easing up from the camera's near plane.
                if (previous.x <= 0.0)
                    return float2(nearest, engaged ? 1.0 : 0.0);

                // Disengaged holds the distance and only lets the engagement fall. Easing
                // the distance somewhere while the blur fades would be a focus sweep that
                // nothing asked for, and it would have to be swept back on the way in.
                float distance = engaged
                    ? lerp(previous.x, nearest, saturate(_FocusLerp))
                    : previous.x;

                float engagement = lerp(previous.y, engaged ? 1.0 : 0.0, saturate(_FocusLerp));

                return float2(distance, engagement);
            }
            ENDHLSL
        }

        // ── Pass 2: colour and circle of confusion, half resolution ─────
        Pass
        {
            Name "NearFieldDepthOfFieldPrepare"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                float3 colour = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv).rgb;

                float coc = CircleOfConfusionAt(uv, SampleFocus());

                // Halved, because everything downstream of here works in half-resolution
                // texels and a radius quoted in full-resolution pixels would reach twice
                // as far as authored.
                return float4(colour, coc * 0.5);
            }
            ENDHLSL
        }

        // ── Pass 3: gather ──────────────────────────────────────────────
        Pass
        {
            Name "NearFieldDepthOfFieldGather"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float2 texel = _DofScreenSize.zw * 2.0;

                float4 centre = SAMPLE_TEXTURE2D(_DofColorTex, sampler_DofColorTex, uv);

                float radius = abs(centre.a);

                float3 sum = centre.rgb;
                float weight = 1.0;

                UNITY_UNROLL
                for (int s = 0; s < GATHER_COUNT; s++)
                {
                    float theta = s * GOLDEN_ANGLE;
                    float spread = sqrt((s + 0.5) * rcp((float)GATHER_COUNT));

                    float2 offset = float2(cos(theta), sin(theta)) * spread;
                    float2 uvS = uv + offset * radius * texel;

                    float4 tap = SAMPLE_TEXTURE2D(_DofColorTex, sampler_DofColorTex, uvS);

                    // A sample only bleeds this far if its OWN circle of confusion
                    // reaches here. Without this test a blurred background washes over a
                    // sharp foreground and every silhouette grows a halo -- which is the
                    // artefact that made the weapon look eaten into before, and it is a
                    // property of gathering, not of this weapon.
                    float reach = abs(tap.a);
                    float distance = spread * radius;

                    float accept = reach >= distance ? 1.0 : 0.0;

                    sum += tap.rgb * accept;
                    weight += accept;
                }

                return float4(sum * rcp(weight), centre.a);
            }
            ENDHLSL
        }

        // ── Pass 4: composite ───────────────────────────────────────────
        Pass
        {
            Name "NearFieldDepthOfFieldComposite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                float4 sharp = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv);

                // Recomputed at full resolution rather than read from the half-size
                // buffer. The blend factor decides where the sharp image survives, and a
                // half-resolution one would let the blurred version win along a
                // one-pixel border of everything.
                float coc = abs(CircleOfConfusionAt(uv, SampleFocus()));

                float3 blurred = SAMPLE_TEXTURE2D(_DofBlurredTex, sampler_DofBlurredTex, uv).rgb;

                // Fully blurred once the circle of confusion passes a pixel, because
                // below that there is nothing to see: a blur of less than one pixel is
                // the sharp image.
                float amount = saturate(coc);

                return float4(lerp(sharp.rgb, blurred, amount), sharp.a);
            }
            ENDHLSL
        }
    }
}

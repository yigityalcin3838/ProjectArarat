using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Where the depth of field is authored AND where gameplay drives it, which is the same
// place on purpose.
//
// It is a VolumeComponent rather than a set of fields on the renderer feature for two
// reasons, and the second one is the deciding one:
//
//   * It is how Unity expects post-processing to be authored, and this project already
//     drives volume components from gameplay -- PlayerPostProcessEffects reaches into
//     Vignette and ChromaticAberration exactly this way.
//
//   * THERE ARE ALREADY TWO PROFILES. DefaultVolumeProfile and
//     DefaultIndoorVolumeProfile. A corridor wants a different focal range from a
//     mountainside, and a volume gives that for free, blended, with no code. Fields on
//     the renderer feature are one global setting for the whole game.
//
// ── THE THREE STATES, AND WHY THEY ARE TWO AXES ──────────────────────────────────────
//
// The behaviours asked for were: autofocus off during a reload with everything but the
// held item blurred; the held item blurred while the walk offset runs, with autofocus
// still working; and autofocus engaging only when something is within a few metres of
// where the player is looking.
//
// Those are not three modes. They are two independent questions, and separating them is
// what keeps this from becoming a list of special cases:
//
//   WHERE IS FOCUS      -- Autofocus (measured, and gated by distance), pinned to the
//                          Foreground, or Manual.
//   IS THE FOREGROUND EXEMPT -- foregroundBlurScale, on its own, because a weapon 30 cm
//                          from the eye is violently out of focus whenever the world is
//                          in focus, and how much of that to actually show is a look
//                          decision rather than a consequence.
//
// So a reload moves focus (mode = Foreground: the item is sharp, the world falls away),
// and the walk offset lifts the exemption (foregroundBlurScale = 1: the item blurs, focus
// carries on measuring). Neither touches the other's parameter, which is why they can
// overlap -- and they do overlap, because reloading while walking is ordinary.
[Serializable, VolumeComponentMenu("Ararat/Near Field Depth of Field")]
public sealed class NearFieldDepthOfFieldVolume : VolumeComponent, IPostProcessComponent
{
    public enum FocusMode
    {
        // Measured off the frame, gated by Autofocus Max Distance.
        Autofocus,

        // Pinned to Foreground Distance. What a reload wants: the item in hand is the
        // only thing sharp.
        Foreground,

        // Pinned to Manual Distance.
        Manual,
    }

    [Serializable]
    public sealed class FocusModeParameter : VolumeParameter<FocusMode>
    {
        public FocusModeParameter(FocusMode value, bool overrideState = false)
            : base(value, overrideState) { }
    }

    // Ten pixels is enough for a reload to drop the room away convincingly without the
    // background turning to soup. Note that a volume override does nothing until its own
    // checkbox is ticked, so the number here is only the starting point.
    [Tooltip("Largest blur radius in pixels at full resolution. 0 switches the effect off.")]
    public ClampedFloatParameter maxBlurRadius = new ClampedFloatParameter(10.0f, 0.0f, 32.0f);

    [Header("Focus")]
    [Tooltip("Autofocus measures off the frame; Foreground pins to the held item; " +
             "Manual pins to a distance.")]
    public FocusModeParameter focusMode = new FocusModeParameter(FocusMode.Autofocus);

    // THE GATE, and it is the reason autofocus does not hunt.
    //
    // BEYOND THIS THE EFFECT STANDS DOWN ENTIRELY: nothing near means a clean frame, the
    // held item included. There was a resting distance here for that job and it was the
    // wrong answer -- parking focus at sixty metres does make the world sharp, but it
    // leaves an item 30 cm from the eye violently blurred, which is not what "everything
    // is sharp" means. So disengaging scales the whole effect to nothing instead, and the
    // focal distance is simply held where it was, ready for the next time something comes
    // close.
    [Tooltip("Autofocus only engages on surfaces nearer than this. Past it the effect " +
             "fades out entirely and the frame is sharp.")]
    public ClampedFloatParameter autofocusMaxDistance = new ClampedFloatParameter(3.0f, 0.2f, 100.0f);

    [Tooltip("How quickly focus follows what it measures, and how quickly the effect " +
             "fades in and out of range, per second.")]
    public ClampedFloatParameter focusSpeed = new ClampedFloatParameter(6.0f, 0.5f, 30.0f);

    [Tooltip("Where on screen autofocus looks, in viewport coordinates.")]
    public Vector2Parameter focusPoint = new Vector2Parameter(new Vector2(0.5f, 0.5f));

    [Tooltip("Radius of the disc autofocus searches, in pixels. It takes the nearest " +
             "surface it finds there.")]
    public ClampedFloatParameter focusSearchRadius = new ClampedFloatParameter(24.0f, 0.0f, 200.0f);

    // The held item is rigid relative to the camera, so its distance is effectively a
    // constant -- the same argument that puts the spill radius in pixels. Authored rather
    // than measured, because measuring it means a reduction over the foreground buffer to
    // recover a number that does not move.
    [Tooltip("Distance to the held item, for Foreground focus. Match it to how far " +
             "ItemHold sits from the camera.")]
    public ClampedFloatParameter foregroundDistance = new ClampedFloatParameter(0.35f, 0.05f, 3.0f);

    [Tooltip("Used by Manual focus.")]
    public ClampedFloatParameter manualDistance = new ClampedFloatParameter(3.0f, 0.05f, 200.0f);

    [Header("Blur")]
    // HALF A METRE, AND IT IS SIZED FOR THE RELOAD. Focus pins to 0.35 m there, so this
    // keeps everything from the lens out to 0.85 m sharp -- which is the whole weapon,
    // muzzle included, and nothing else. A tighter range would start blurring the far end
    // of the barrel, which reads as a fault rather than as focus.
    [Tooltip("Metres either side of the focal plane that stay sharp.")]
    public ClampedFloatParameter focusRange = new ClampedFloatParameter(0.5f, 0.0f, 20.0f);

    // TWO METRES, and it follows from the gate above. Autofocus only engages inside three
    // metres, so the whole useful range of this effect is about that wide: past the sharp
    // half-metre, two more metres of ramp puts anything beyond roughly three metres at
    // full blur. Four metres of falloff -- the first figure here -- meant a room lit up at
    // barely half strength and the effect read as a smudge instead of a focus.
    [Tooltip("Metres beyond that range over which the blur ramps to its maximum.")]
    public ClampedFloatParameter blurFalloff = new ClampedFloatParameter(2.0f, 0.05f, 50.0f);

    // Separate from everything above because it answers the other question. At 0 the held
    // item never blurs whatever focus is doing; at 1 it blurs exactly as much as its
    // distance says it should.
    [Tooltip("How much of the held item's own blur to actually show. Low keeps it " +
             "readable; 1 is what the walk offset wants.")]
    public ClampedFloatParameter foregroundBlurScale = new ClampedFloatParameter(0.15f, 0.0f, 1.0f);

    // Zero radius is off, and off has to mean "do not enqueue" rather than "blur by
    // nothing" -- five passes that produce the original image are five passes wasted.
    public bool IsActive() => maxBlurRadius.value > 0.0f;

    public bool IsTileCompatible() => false;
}

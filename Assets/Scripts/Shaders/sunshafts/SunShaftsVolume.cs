using System;
using UnityEngine;
using UnityEngine.Rendering;

// Volumetric light shafts, ray-marched against the main light's shadow map.
// Unlike a screen-space radial blur, this doesn't need the sun to be on
// screen -- it asks "is this bit of air lit?" for points in front of the
// camera, so a beam coming through a window shows up while facing away from
// the sun entirely.
[Serializable, VolumeComponentMenu("Post-processing/Sun Shafts")]
public class SunShaftsVolume : VolumeComponent
{
    [Tooltip("Overall strength. Defaults to 0/off -- IsActive() gates on this single field, " +
             "the same convention as the other custom post-processing volumes here.")]
    public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 5f);

    [Tooltip("Tints the shafts on top of the sun's own colour.")]
    public ColorParameter shaftColor = new ColorParameter(Color.white, true, false, true);

    [Tooltip("How much light the air scatters, per metre. This is the main dial for how thick the " +
             "beams look.")]
    public ClampedFloatParameter density = new ClampedFloatParameter(0.05f, 0f, 3f);

    [Tooltip("How much the scattering favours the direction of the light. 0 scatters evenly in " +
             "all directions; higher makes beams flare up strongly when looking toward the sun " +
             "and stay subtle when looking away, which is how real haze behaves. Raise it to " +
             "quieten the effect outdoors while keeping it where you're looking at the light.\n\n" +
             "Stops just short of 1 on purpose: the phase function's numerator is (1 - g*g), so " +
             "at exactly 1 it collapses to zero and the whole effect disappears rather than " +
             "getting sharper.")]
    public ClampedFloatParameter forwardScattering = new ClampedFloatParameter(0.4f, 0f, 0.99f);

    [Tooltip("Ceiling on that forward flare. Looking straight at the sun is where the phase " +
             "function spikes hardest, which is the bright blob around the sun -- capping it " +
             "removes that without touching the beams, since those come from shadow contrast " +
             "rather than from viewing angle. Set to 1 for no flare at all; raise it to let some " +
             "back in.")]
    public ClampedFloatParameter maxForwardBoost = new ClampedFloatParameter(3f, 1f, 20f);

    [Tooltip("How far along each ray to march, in metres. There's no point setting this beyond " +
             "the pipeline's Shadow Distance -- past that there's no shadow map to test against, " +
             "so the shafts just stop.")]
    public ClampedFloatParameter maxDistance = new ClampedFloatParameter(50f, 1f, 200f);

    [Tooltip("Samples per ray. More is smoother but costs proportionally; the dithering below " +
             "hides a lot, so low counts hold up better than they sound.")]
    public ClampedIntParameter steps = new ClampedIntParameter(24, 8, 64);

    [Tooltip("Resolution divider for the ray-march. This is by far the biggest performance dial " +
             "-- the result is soft, so half or quarter resolution is hard to tell apart.")]
    public ClampedIntParameter downsample = new ClampedIntParameter(2, 1, 4);

    public bool IsActive() => intensity.value > 0f;
}

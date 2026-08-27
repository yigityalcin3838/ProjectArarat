using System;
using UnityEngine;
using UnityEngine.Rendering;

// Exponential Height Fog, same parameter naming as Unreal/Flax's version of
// this (Density, Height Falloff, Color, Start Distance, Cutoff Distance,
// Max Opacity), extended to also fog the skybox using the same physical
// model (see CustomFog.shader) -- neither of those engines' versions touch
// the sky, but the analytical formula this is built on extends to it
// cleanly, so it's included here.
[Serializable, VolumeComponentMenu("Post-processing/Custom Fog")]
public class CustomFogVolume : VolumeComponent
{
    public ColorParameter fogColor = new ColorParameter(new Color(0.65f, 0.72f, 0.8f), true, false, true);

    // Fog thickness at world height 0. Defaults to 0 (off) -- IsActive()
    // gates on this field, so leaving every override unticked on a profile
    // truly means no fog, the same way ScreenSharpeningVolume's intensity
    // does.
    public MinFloatParameter density = new MinFloatParameter(0f, 0f);

    // Controls how fast density drops off with height. Smaller = fog stays
    // thick over a taller range (a hazier, less sharply-layered look);
    // larger = a thin, sharply ground-hugging layer.
    public MinFloatParameter heightFalloff = new MinFloatParameter(0.02f, 0.0001f);

    // No fog closer than this (world units) -- lets nearby geometry stay
    // clear instead of the fog already eating into it right at the camera.
    public MinFloatParameter startDistance = new MinFloatParameter(0f, 0f);

    // Beyond this distance, fog is skipped entirely (e.g. to keep a distant
    // background mountain silhouette clear of it). 0 = no cutoff.
    public MinFloatParameter cutoffDistance = new MinFloatParameter(0f, 0f);

    // Fog never gets more opaque than this, so distant shapes always show
    // through at least a little instead of the world vanishing into a wall
    // of solid fog color.
    public ClampedFloatParameter maxOpacity = new ClampedFloatParameter(1f, 0f, 1f);

    public bool IsActive() => density.value > 0f;
}

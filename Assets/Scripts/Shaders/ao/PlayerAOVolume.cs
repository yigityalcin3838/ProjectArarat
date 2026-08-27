using System;
using UnityEngine;
using UnityEngine.Rendering;

// Custom screen-space AO that ONLY affects Layer "Player" geometry. Built-in
// URP SSAO is disabled for that layer separately (see PlayerAOFeature's
// pass, which overwrites the built-in AO texture at Player-mask pixels
// instead of layering on top of it), so this is the sole source of ambient
// occlusion on the player -- there's no double-darkening to balance against.
[Serializable, VolumeComponentMenu("Post-processing/Player Ambient Occlusion")]
public class PlayerAOVolume : VolumeComponent
{
    // World-space sample radius for the hemisphere occlusion test.
    public MinFloatParameter radius = new MinFloatParameter(0.3f, 0.01f);

    // Defaults to 0/off -- IsActive() gates on this single field, the same
    // convention as the other custom post-processing Volume Components in
    // this project (see ScreenSharpeningVolume, CustomFogVolume).
    public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 4f);

    // Contrast curve on the raw occlusion estimate before it's applied.
    public ClampedFloatParameter power = new ClampedFloatParameter(1.5f, 0.5f, 4f);

    // Extra darkening along sharp silhouette/crease edges (depth and normal
    // discontinuities), layered on top of the hemisphere occlusion above --
    // an outline-like line right where the player's shape actually breaks,
    // which plain hemisphere sampling alone tends to miss since it needs
    // nearby occluding geometry, not just a normal pointing a different way.
    public ClampedFloatParameter outlineIntensity = new ClampedFloatParameter(0f, 0f, 1f);
    // How many pixels out the edge test looks -- thicker outline, but also
    // more prone to catching detail that isn't really a silhouette edge.
    public MinFloatParameter outlineThickness = new MinFloatParameter(1.5f, 0.5f);

    public bool IsActive() => intensity.value > 0f;
}

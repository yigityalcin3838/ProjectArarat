using System;
using UnityEngine.Rendering;

[Serializable, VolumeComponentMenu("Post-processing/Screen Sharpening")]
public class ScreenSharpeningVolume : VolumeComponent
{
    public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 5f);

    public bool IsActive() => intensity.value > 0f;
}

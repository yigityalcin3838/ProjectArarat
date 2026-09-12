using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// The lens held items are drawn through, when something other than the renderer asset
// wants a say in it.
//
// ── WHY THIS EXISTS, GIVEN THE FIGURE ALREADY HAD A HOME ─────────────────────────────
//
// ViewModelLensFeature carries one field and that is deliberately the base: one lens for
// every held item, authored in one place. There was a version with a per-weapon figure
// and an aim figure pushed in through a static, and it was removed because a value three
// things could write was three places to look when the weapon came out wrong.
//
// What brings it back is narrower than that. Aiming is a moment when the weapon is
// deliberately held up to the eye, and how much the lens should tighten for it is a fact
// about the weapon -- a pistol at arm's length and a rifle against the cheek do not want
// the same one. That is a real per-weapon figure, and the base is still not.
//
// So it is BORROWED rather than owned: the weapon overrides this while its sights are up
// and releases it when they come down, and the renderer asset's figure stands the rest of
// the time. The same arrangement as the depth of field's autofocus range, and for the
// same reason -- a volume is the one mechanism where a value can be handed back.
//
// A volume rather than a static because a static has no notion of handing anything back,
// and because this way an indoor profile can disagree about the base without any of it
// reaching gameplay.
[Serializable, VolumeComponentMenu("Ararat/Near Field Lens")]
public sealed class NearFieldLensVolume : VolumeComponent, IPostProcessComponent
{
    // ZERO MEANS "NOT SET", and the sentinel is a value rather than the parameter's own
    // override flag on purpose: the flag says whether some volume wrote it, which is not
    // the same question as whether anybody meant to. A weapon with no aim figure of its
    // own pushes zero and the base stands.
    //
    // Note this is not the feature's "0 uses the camera's field of view" -- that reading
    // belongs to the base figure. Here zero only means the base is not being overridden.
    [Tooltip("Field of view to draw held items at while something is overriding it. " +
             "0 leaves the renderer feature's own figure in force.")]
    public ClampedFloatParameter viewModelFov = new ClampedFloatParameter(0.0f, 0.0f, 120.0f);

    public bool IsActive() => viewModelFov.value > 0.0f;

    public bool IsTileCompatible() => false;
}

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PlayerPostProcessEffects : MonoBehaviour
{
    [SerializeField] private PlayerStamina stamina;
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private Volume volume;

    // There was an autofocus here once that cast a physics ray down the aim direction,
    // and it is gone along with URP's depth of field. Both were replaced rather than
    // repaired: the ray disagreed with the picture in every case that mattered -- it
    // passed through anything without a collider, stopped on triggers and found the
    // player's own capsule -- and URP's effect blurred the held weapon's silhouette with
    // the background's depth, because the weapon is drawn through a lens of its own and
    // its depths do not describe where its pixels are.
    //
    // NearFieldDepthOfField replaces both. It measures off the frame, so what it focuses
    // on is whatever was actually drawn, and it composites the held item's own depth over
    // the world's first so the item is somewhere coherent.
    //
    // What is left here is the gameplay half: this script is the one place gameplay
    // reaches into post-processing, and the depth of field now has states that gameplay
    // owns.

    [Header("Low Stamina Effect")]
    [SerializeField] private float lowStaminaThresholdRatio = 0.25f;
    [SerializeField] private float lowStaminaVignetteIntensity = 0.4f;
    [SerializeField] private float lowStaminaVignetteSmoothness = 0.5f;
    [SerializeField] private float lowStaminaChromaticAberration = 0.5f;

    [Header("Crouch Effect")]
    [SerializeField] private float crouchVignetteIntensity = 0.15f;
    [SerializeField] private float crouchVignetteSmoothness = 0.3f;

    [Header("Aim Effect")]
    [SerializeField] private float aimVignetteIntensity = 0.15f;
    [SerializeField] private float aimVignetteSmoothness = 0.3f;

    [Header("Smoothing")]
    [SerializeField] private float effectSmoothSpeed = 4f;

    // ── Depth of field ───────────────────────────────────────────────────────────────
    //
    // Two states, and they are deliberately not one. A reload moves WHERE FOCUS IS: the
    // item comes up to be worked on, so focus pins to it and the world falls away. The
    // walk offset lifts the item's EXEMPTION FROM BLUR: focus carries on measuring the
    // world, and the item -- which is 30 cm from the eye and therefore violently out of
    // focus whenever the world is not -- stops being protected from that.
    //
    // Kept apart because they overlap. Reloading while walking is ordinary, and a single
    // "depth of field mood" enum would have to invent an answer for it.
    [Header("Depth of Field")]
    [Tooltip("How much of the held item's blur to show while nothing is asking for more.")]
    [SerializeField] private float restingForegroundBlur = 0.15f;

    // A FLOOR, not a scale, and that distinction is the whole reason both behaviours can
    // be on at once. The carry's blur does not go through the focus path: out in the open
    // nothing is inside the autofocus gate, the effect stands down so the field reads
    // clean, and anything riding on that engagement went down with it. This rides on
    // nothing and the item takes whichever blur is larger.
    //
    // Two figures, because a run carry is a looser one than a walk carry and reads as
    // more motion. Neither is driven by the legs -- see ItemCarry.
    [Tooltip("Blur the held item carries in its walking offset, as a fraction of the " +
             "maximum radius. Independent of focus.")]
    [SerializeField] private float walkCarryBlur = 0.5f;

    [Tooltip("The same, for the running offset.")]
    [SerializeField] private float runCarryBlur = 1.0f;

    [Tooltip("Seconds-scale smoothing on the held item's blur, so it eases in with the " +
             "walk rather than snapping on with the first footstep.")]
    [SerializeField] private float foregroundBlurSmoothSpeed = 6.0f;

    // Down the sights the gate comes off. Autofocus normally stands down past a few
    // metres so open ground reads clean, but aiming is the one time the player is
    // deliberately looking at one distant thing -- so focus follows it however far it is,
    // and the authored focal range then leaves a narrow sharp slab around it and blurs
    // the rest. Which is what "only where the crosshair is looking" means at range.
    [Tooltip("Autofocus range while aiming down sights, in metres. Large enough that the " +
             "gate effectively comes off.")]
    [SerializeField] private float aimAutofocusMaxDistance = 500.0f;

    private Vignette _vignette;
    private ChromaticAberration _chromaticAberration;

    // NOT FETCHED OUT OF THE AUTHORED PROFILE, which is what the first version of this
    // did and it silently did nothing. The depth of field override belongs on a scene
    // profile -- that is where an artist puts it, and the project already has an indoor
    // one and an outdoor one -- so reaching for it through the player's own volume found
    // nothing and every gameplay state was dropped on the floor.
    //
    // This one is created here instead, at a high priority, holding only the parameters
    // gameplay owns. A volume parameter that has not been overridden does not take part
    // in the blend, so the authored profiles keep deciding the look and this decides the
    // state -- and neither has to know the other exists.
    private Volume _depthOfFieldVolume;
    private VolumeProfile _depthOfFieldProfile;
    private NearFieldDepthOfFieldVolume _depthOfField;
    private NearFieldLensVolume _lens;

    private float _baseVignetteIntensity;
    private float _baseVignetteSmoothness;
    private float _foregroundBlur;

    private bool _isAiming;
    private bool _isReloading;
    private ItemCarry _itemCarry;

    // Lets an equipped item (e.g. Weapon) drive the aim vignette while it's
    // active, without this script needing to know anything about items -- same
    // push-values-in pattern as PlayerLook's FOV override.
    public void SetAiming(bool isAiming) => _isAiming = isAiming;

    // Pushed in by the item that is reloading, for the same reason as the line above:
    // this script stays ignorant of items, and the item already knows.
    public void SetReloading(bool isReloading) => _isReloading = isReloading;

    // How the held item is being carried, pushed in by the item itself.
    //
    // AN ENUM AND NOT AN AMOUNT, which is the division everything here follows: the item
    // knows which carry it is in -- only it can, since the pose is cleared by a shot, the
    // sights or a reload -- and this script decides what that should look like. An item
    // pushing a blur strength would be an item with opinions about post-processing.
    //
    // It replaced a magnitude measured off HandMotion's bob. That read movement, and
    // movement is the wrong question: a weapon held ready while the legs are moving is
    // still held ready, and softening it blurs the thing the player is looking at.
    public enum ItemCarry
    {
        // Up and usable -- at the hip, aiming, firing, pulled in by a wall.
        Ready,

        // Dropped into the walking offset.
        Walk,

        // Dropped into the running offset.
        Run,
    }

    public void SetItemCarry(ItemCarry carry) => _itemCarry = carry;

    // The lens the held item is drawn through, while an item wants a say in it.
    //
    // Pushed in by the item, like everything else here, and zero hands it back -- the
    // renderer asset's own figure is the base and the item only borrows it. That is the
    // whole reason this goes through a volume: a static could set the figure but had no
    // way of saying "and stop".
    public void SetViewModelFovOverride(float fov) => _viewModelFovOverride = Mathf.Max(fov, 0f);

    private float _viewModelFovOverride;

    private void Awake()
    {
        if (volume != null && volume.profile != null)
        {
            volume.profile.TryGet(out _vignette);
            volume.profile.TryGet(out _chromaticAberration);
        }

        CreateDepthOfFieldVolume();

        // Read off the profile rather than written into it, so whatever the volume was
        // authored at is the value everything below returns to.
        if (_vignette != null)
        {
            _baseVignetteIntensity = _vignette.intensity.value;
            _baseVignetteSmoothness = _vignette.smoothness.value;
        }
    }

    private void Update()
    {
        UpdateDepthOfField();

        if (_vignette == null && _chromaticAberration == null)
            return;

        float staminaAmount = 0f;
        if (stamina != null)
        {
            float threshold = stamina.MaxStamina * lowStaminaThresholdRatio;
            staminaAmount = threshold > 0f ? Mathf.Clamp01(1f - stamina.CurrentStamina / threshold) : 0f;
        }

        float crouchAmount = movement != null && movement.IsCrouching ? 1f : 0f;
        float aimAmount = _isAiming ? 1f : 0f;

        if (_vignette != null)
        {
            float targetIntensity = _baseVignetteIntensity + Mathf.Max(staminaAmount * lowStaminaVignetteIntensity, Mathf.Max(crouchAmount * crouchVignetteIntensity, aimAmount * aimVignetteIntensity));
            float targetSmoothness = _baseVignetteSmoothness + Mathf.Max(staminaAmount * lowStaminaVignetteSmoothness, Mathf.Max(crouchAmount * crouchVignetteSmoothness, aimAmount * aimVignetteSmoothness));

            _vignette.intensity.value = Mathf.Lerp(_vignette.intensity.value, targetIntensity, effectSmoothSpeed * Time.deltaTime);
            _vignette.smoothness.value = Mathf.Lerp(_vignette.smoothness.value, targetSmoothness, effectSmoothSpeed * Time.deltaTime);
        }

        if (_chromaticAberration != null)
        {
            float targetChromatic = staminaAmount * lowStaminaChromaticAberration;
            _chromaticAberration.intensity.value = Mathf.Lerp(_chromaticAberration.intensity.value, targetChromatic, effectSmoothSpeed * Time.deltaTime);
        }
    }

    // A global volume of its own rather than a component on the player's, because this
    // has to win the blend against whatever the scene authored, and priority is how a
    // volume says so.
    private void CreateDepthOfFieldVolume()
    {
        _depthOfFieldProfile = ScriptableObject.CreateInstance<VolumeProfile>();
        _depthOfFieldProfile.name = "Near Field Depth of Field (Runtime)";

        // False: every parameter starts un-overridden, so nothing here touches the blend
        // until this script writes it. The alternative would have this volume asserting
        // its own defaults over the scene's authored ones the moment the player spawns.
        _depthOfField = _depthOfFieldProfile.Add<NearFieldDepthOfFieldVolume>(overrides: false);

        // The lens rides in the same profile. One runtime volume for everything gameplay
        // borrows from post-processing, rather than one per effect -- they all have the
        // same lifetime and the same priority, and splitting them would only be more
        // objects saying the same thing about when they apply.
        _lens = _depthOfFieldProfile.Add<NearFieldLensVolume>(overrides: false);

        var host = new GameObject("Near Field Depth of Field (Runtime)");
        host.transform.SetParent(transform, worldPositionStays: false);

        // ON THE SAME LAYER AS THE SCENE'S OWN VOLUME, and this is the difference between
        // this working and doing nothing at all.
        //
        // A Volume only reaches a camera whose Volume Mask includes its layer. A new
        // GameObject is born on Default, so if the project keeps its volumes on a layer of
        // their own -- which is the usual arrangement, and what the camera's mask is for
        // -- this one is invisible to every camera and every value pushed into it is
        // silently discarded. Nothing errors; the effect simply never changes.
        //
        // Derived from the volume already wired to this component rather than authored as
        // a second field: that object's layer is demonstrably one the camera sees, since
        // the vignette driven through it arrives. Stating the layer twice is how the two
        // end up disagreeing.
        host.layer = volume != null ? volume.gameObject.layer : gameObject.layer;

        // Not saved and not shown: it is an implementation detail of this component, and
        // a stray one left in a scene would override the profile everywhere with whatever
        // state the player was in when it was saved.
        host.hideFlags = HideFlags.HideAndDontSave;

        _depthOfFieldVolume = host.AddComponent<Volume>();
        _depthOfFieldVolume.isGlobal = true;
        _depthOfFieldVolume.priority = 1000.0f;
        _depthOfFieldVolume.weight = 1.0f;
        _depthOfFieldVolume.profile = _depthOfFieldProfile;
    }

    private void OnDestroy()
    {
        // Created with CreateInstance, so nothing else will collect it.
        if (_depthOfFieldProfile != null)
            Destroy(_depthOfFieldProfile);
    }

    private void UpdateDepthOfField()
    {
        // Overridden only while an item is asking, and released otherwise so the renderer
        // asset's base comes back -- the same borrow-and-return the aim gate below uses.
        if (_lens != null)
        {
            _lens.viewModelFov.overrideState = _viewModelFovOverride > 0f;
            _lens.viewModelFov.value = _viewModelFovOverride;
        }

        if (_depthOfField == null)
            return;

        // A reload takes focus off the world entirely. Not by switching autofocus off and
        // leaving it wherever it stood -- that would keep whatever the player happened to
        // be looking at -- but by pinning focus to the item, which is the thing being
        // looked at while it is being worked on.
        _depthOfField.focusMode.overrideState = true;
        _depthOfField.focusMode.value = _isReloading
            ? NearFieldDepthOfFieldVolume.FocusMode.Foreground
            : NearFieldDepthOfFieldVolume.FocusMode.Autofocus;

        // The focus-derived share stays where the profile put it. Only the floor moves,
        // because only the floor is about the hands being carried.
        _depthOfField.foregroundBlurScale.overrideState = true;
        _depthOfField.foregroundBlurScale.value = restingForegroundBlur;

        // RELEASED DURING A RELOAD, because a reload wants the item sharp and the floor is
        // the one thing that would blur it anyway -- focus is pinned to it, so its
        // focus-derived blur is already nothing.
        //
        // In practice a reload also clears the carry outright, so this is belt and braces
        // rather than the only guard. It is kept because the two facts are independent:
        // whether the item is down is the item's business, and whether a reload should
        // soften it is this script's.
        float carryFloor = _itemCarry switch
        {
            ItemCarry.Run => runCarryBlur,
            ItemCarry.Walk => walkCarryBlur,
            _ => 0.0f,
        };

        float targetFloor = _isReloading ? 0.0f : carryFloor;

        // Eased rather than set, so the item's blur arrives with the walk. Snapping it on
        // at the first frame of movement reads as a glitch, which is the same reason the
        // vignettes above are lerped.
        _foregroundBlur = Mathf.Lerp(
            _foregroundBlur, targetFloor, foregroundBlurSmoothSpeed * Time.deltaTime);

        _depthOfField.foregroundBlurFloor.overrideState = true;
        _depthOfField.foregroundBlurFloor.value = _foregroundBlur;

        // OVERRIDDEN ONLY WHILE AIMING, and released the rest of the time so the scene's
        // own figure comes back. That is the whole reason to drive this through a volume
        // rather than by writing numbers into the authored profile: aiming borrows one
        // parameter and hands it back, and a corridor and a mountainside can still
        // disagree about what the gate should be when nobody is aiming.
        _depthOfField.autofocusMaxDistance.overrideState = _isAiming;

        if (_isAiming)
            _depthOfField.autofocusMaxDistance.value = aimAutofocusMaxDistance;

        // Everything else stays authored -- the focal range, the falloff, the gate when
        // not aiming, the blur radius. This method only writes what gameplay knows and a
        // profile cannot: which state the player is in. How that state should LOOK is
        // still a profile away from being retuned, per area, without touching this file.
    }
}

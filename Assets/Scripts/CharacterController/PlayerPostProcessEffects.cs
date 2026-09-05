using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PlayerPostProcessEffects : MonoBehaviour
{
    [SerializeField] private PlayerStamina stamina;
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private Volume volume;

    // For the ray down the middle of the rendered image, which is what autofocus
    // measures against. The same ray the weapon fires along, so the lens focuses on
    // exactly what a shot would hit.
    [SerializeField] private PlayerLook playerLook;

    [Header("Autofocus")]
    // Focus follows whatever is being looked at whenever nothing else is claiming the
    // lens. Looking at something close throws the background out; looking past it
    // brings the background back, and the rack between them is the effect.
    //
    // Off, focus distance is the volume's to author, which is the old behaviour.
    [SerializeField] private bool autofocus = true;

    // How close something has to be before autofocus takes an interest.
    //
    // A gate rather than a ceiling. Inside it there is something specific to look at
    // and pulling focus onto it is the whole effect; past it the view is a landscape,
    // where a focus plane picked off whatever the crosshair happens to touch is both
    // arbitrary and invisible -- so focus is handed back to the volume and stays
    // wherever it was authored.
    [SerializeField] private float autofocusRange = 10f;

    // A floor on how close it will focus, so a wall walked into does not pull focus
    // to a hand's breadth and blur the entire frame.
    [SerializeField] private float autofocusMinDistance = 0.4f;

    // The lens autofocus brings with it, not just where it points.
    //
    // This is what makes the range gate mean anything. Focus distance on its own
    // changes nothing at a narrow aperture -- everything past a metre or two is
    // inside the depth of field regardless of where the plane sits -- so the volume
    // can stay dialled to whatever the world should normally look like, and these
    // two are the shallow lens that swaps in only while something close has taken
    // focus. Out of range they swap straight back out.
    //
    // Millimetres and an f-stop: longer and wider-open both mean more blur, and both
    // want to be well past what the volume holds for the change to be visible.
    [SerializeField] private float autofocusFocalLength = 70f;
    [SerializeField] private float autofocusAperture = 2.2f;

    // How quickly the whole lens changes, focus and all, on the way IN -- something
    // has come into range and is being racked onto.
    //
    // Deliberately slower than the aim and reload speeds: those are the lens being
    // told where to go, this is it hunting, and a lens that snapped instantly to
    // every glance would read as a glitch rather than as focus.
    [SerializeField] private float autofocusInSpeed = 3.5f;

    // And on the way OUT -- focus travelling to something further away, or back to
    // whatever the volume holds when nothing is in range at all.
    //
    // Its own figure because the two are not the same event. Pulling in is decisive:
    // something specific has been looked at and it is right there. Letting out is
    // not -- what the eye moves to next is further off and less definite, and a
    // slower release is what stops the frame snapping every time the player glances
    // past the edge of something.
    [SerializeField] private float autofocusOutSpeed = 2f;

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

    // Bokeh's own three fields, so the volume's Depth Of Field has to be in Bokeh
    // mode -- Gaussian ignores all of them.
    //
    // Focus distance is metres to the sharp plane; aperture is an f-stop, where
    // smaller is a shallower field and more blur; focal length is millimetres, where
    // longer is also more blur and a tighter-looking frame. Between them, pulling the
    // focus in to about where the weapon is and opening the aperture is what leaves
    // the world soft behind sharp hands.
    // Aiming: sharp only at the sights, soft on both sides of them. The focus sits
    // out at roughly where the front sight is, and the field is made as shallow as
    // it goes -- widest aperture, longest lens -- so the back of the weapon falls
    // out of focus in front of that plane and the world falls out behind it.
    //
    // That both ends blur is the point. A deep field would keep the rear of the
    // weapon sharp too, and the eye would have no reason to settle anywhere.
    [Header("Aim Depth Of Field")]
    [SerializeField] private float aimDofFocusDistance = 0.55f;
    [SerializeField] private float aimDofAperture = 1.8f;
    [SerializeField] private float aimDofFocalLength = 60f;
    [SerializeField] private float aimDofSmoothSpeed = 6f;

    // Kept apart from aiming rather than shared, because the two are looking at
    // different things: down the sights the eye is out at the target, mid-reload it
    // is down at the hands. Aiming is also the longer-held of the two and wants the
    // gentler settle.
    //
    // They cannot both apply -- a reload refuses aiming outright -- so there is no
    // blend between them to worry about, only which one is running.
    // Reloading: the whole of the near work sharp, everything past it soft. Focus
    // sits behind the weapon rather than on it and the field is deliberately deeper
    // -- a narrower aperture, a shorter lens -- so the weapon and both hands sit
    // inside the sharp zone together while the world a couple of metres out goes.
    //
    // The opposite trade to aiming, and for the opposite reason: there the eye is
    // meant to pick one plane, here it is meant to take in a whole action.
    [Header("Reload Depth Of Field")]
    [SerializeField] private float reloadDofFocusDistance = 0.9f;
    [SerializeField] private float reloadDofAperture = 4f;
    [SerializeField] private float reloadDofFocalLength = 45f;
    [SerializeField] private float reloadDofSmoothSpeed = 8f;

    // The walking carry's own lens, ranked last of the four -- below the sights, a
    // reload, and autofocus.
    //
    // Below autofocus deliberately. Walking is most of what the player ever does, so
    // ranking it higher would switch autofocus off almost permanently; down here it
    // is what the walk looks like when there is nothing close enough to focus on,
    // which is exactly the case the volume's own settings would otherwise cover.
    //
    // So it is the neutral look with a walk-specific version, not a replacement for
    // looking at things.
    [Header("Walk Depth Of Field")]
    [SerializeField] private float walkDofFocusDistance = 3f;
    [SerializeField] private float walkDofAperture = 4f;
    [SerializeField] private float walkDofFocalLength = 40f;
    [SerializeField] private float walkDofSmoothSpeed = 4f;

    [Header("Smoothing")]
    [SerializeField] private float effectSmoothSpeed = 4f;

    private Vignette _vignette;
    private ChromaticAberration _chromaticAberration;
    private DepthOfField _depthOfField;
    private float _baseVignetteIntensity;
    private float _baseVignetteSmoothness;
    private float _baseDofAperture;
    private float _baseDofFocalLength;

    private float _baseDofFocusDistance;
    private bool _isAiming;
    private bool _isReloading;
    private bool _isInWalkPose;
    private bool _isDrivingDof;

    // Focal length is in millimetres and the other two are far smaller numbers, so
    // this is loose enough for the largest of them and still invisible on it.
    private const float DofSettleThreshold = 0.01f;

    // Lets an equipped item (e.g. Weapon) drive the aim vignette while it's
    // active, without this script needing to know anything about items -- same
    // push-values-in pattern as PlayerLook's FOV override.
    public void SetAiming(bool isAiming) => _isAiming = isAiming;

    public void SetReloading(bool isReloading) => _isReloading = isReloading;

    public void SetInWalkPose(bool isInWalkPose) => _isInWalkPose = isInWalkPose;

    private void Awake()
    {
        if (volume != null && volume.profile != null)
        {
            volume.profile.TryGet(out _vignette);
            volume.profile.TryGet(out _chromaticAberration);
            volume.profile.TryGet(out _depthOfField);
        }

        if (_vignette != null)
        {
            _baseVignetteIntensity = _vignette.intensity.value;
            _baseVignetteSmoothness = _vignette.smoothness.value;
        }

        // Read from the profile rather than written into it, so whatever the volume
        // was authored with is what everything returns to -- focus included, now that
        // it is only claimed while something is close enough to claim it.
        if (_depthOfField != null)
        {
            _baseDofFocusDistance = _depthOfField.focusDistance.value;
            _baseDofAperture = _depthOfField.aperture.value;
            _baseDofFocalLength = _depthOfField.focalLength.value;
        }
    }

    private void Update()
    {
        if (_vignette == null && _chromaticAberration == null && _depthOfField == null)
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

        if (_depthOfField != null)
            UpdateDepthOfField();
    }

    // The lens is one decision with four claimants, ranked.
    //
    // A reload, then the sights, then autofocus finding something inside its range --
    // and if none of them, the volume's own settings, which is the look the world has
    // when nothing is going on. Each claimant brings a whole lens: where it is
    // focused AND what it is. That is the part that matters, because focus distance
    // on its own does nothing at a narrow aperture; the volume can be dialled to keep
    // everything sharp and autofocus swaps in a shallow lens only while it has
    // something to be shallow about.
    //
    // The flag is what lets the volume stay authorable. With no claimant this stops
    // writing entirely and re-reads the volume as the base, so its values are the
    // ones in effect and can be edited during play without being overwritten a frame
    // later -- and whatever they are is what the next claim returns to. The base must
    // only be read while NOT driving, or the return would sample the value it is
    // writing and the lens would chase itself.
    private void UpdateDepthOfField()
    {
        // Measured before anything else so the debug readout is honest about range
        // even while a reload or the sights are holding the lens elsewhere.
        float measured = 0f;
        bool hasSubject = autofocus && TryMeasureLookDistance(out measured);

        bool autofocusEngaged = hasSubject && !_isReloading && !_isAiming;
        bool wantsOverride = _isReloading || _isAiming || autofocusEngaged || _isInWalkPose;

        if (wantsOverride)
            _isDrivingDof = true;

        if (!_isDrivingDof)
        {
            _baseDofFocusDistance = _depthOfField.focusDistance.value;
            _baseDofAperture = _depthOfField.aperture.value;
            _baseDofFocalLength = _depthOfField.focalLength.value;
            return;
        }

        // The rate travels with the target rather than being fixed, so each state
        // arrives at its own pace; going back to neutral uses the shared speed every
        // other effect here relaxes on.
        float targetFocusDistance;
        float targetAperture;
        float targetFocalLength;
        float dofSpeed;

        if (_isReloading)
        {
            targetFocusDistance = reloadDofFocusDistance;
            targetAperture = reloadDofAperture;
            targetFocalLength = reloadDofFocalLength;
            dofSpeed = reloadDofSmoothSpeed;
        }
        else if (_isAiming)
        {
            targetFocusDistance = aimDofFocusDistance;
            targetAperture = aimDofAperture;
            targetFocalLength = aimDofFocalLength;
            dofSpeed = aimDofSmoothSpeed;
        }
        else if (autofocusEngaged)
        {
            targetFocusDistance = measured;
            targetAperture = autofocusAperture;
            targetFocalLength = autofocusFocalLength;

            // Which way focus is travelling, not whether autofocus has just taken
            // over. Almost everything indoors is inside the range, so autofocus is
            // engaged nearly all the time and the handover hardly ever happens --
            // pulling in and letting out are the two things that actually do.
            dofSpeed = targetFocusDistance < _depthOfField.focusDistance.value
                ? autofocusInSpeed
                : autofocusOutSpeed;
        }
        else if (_isInWalkPose)
        {
            targetFocusDistance = walkDofFocusDistance;
            targetAperture = walkDofAperture;
            targetFocalLength = walkDofFocalLength;
            dofSpeed = walkDofSmoothSpeed;
        }
        else
        {
            // Nothing in range: the volume's own lens, arrived at on the out speed.
            //
            // Which is the figure to reach for if looking up at a horizon snaps sharp
            // rather than easing in. All three values travel together at this rate,
            // and it is the whole of that transition -- the lens flattening back out
            // and focus running to the authored distance are one movement, and a
            // quick one has them both over before anything can be watched happening.
            targetFocusDistance = _baseDofFocusDistance;
            targetAperture = _baseDofAperture;
            targetFocalLength = _baseDofFocalLength;
            dofSpeed = autofocusOutSpeed;
        }

        float t = dofSpeed * Time.deltaTime;

        _depthOfField.focusDistance.value = Mathf.Lerp(_depthOfField.focusDistance.value, targetFocusDistance, t);
        _depthOfField.aperture.value = Mathf.Lerp(_depthOfField.aperture.value, targetAperture, t);
        _depthOfField.focalLength.value = Mathf.Lerp(_depthOfField.focalLength.value, targetFocalLength, t);

        // Snapped and handed back once the return has effectively finished. A lerp
        // only ever approaches, so without this it would go on writing fractionally
        // different values forever and the volume would never get its lens back.
        if (wantsOverride)
            return;

        if (Mathf.Abs(_depthOfField.focusDistance.value - targetFocusDistance) < DofSettleThreshold
            && Mathf.Abs(_depthOfField.aperture.value - targetAperture) < DofSettleThreshold
            && Mathf.Abs(_depthOfField.focalLength.value - targetFocalLength) < DofSettleThreshold)
        {
            _depthOfField.focusDistance.value = targetFocusDistance;
            _depthOfField.aperture.value = targetAperture;
            _depthOfField.focalLength.value = targetFocalLength;
            _isDrivingDof = false;
        }
    }

    // How far away whatever is in the middle of the screen is, and whether it is close
    // enough to be worth focusing on at all.
    //
    // Down PlayerLook's aim ray rather than the camera's transform, so focus lands on
    // what the crosshair covers whatever the camera rig is doing to put it there --
    // and on exactly what a shot would hit. Debris and movement capsules are excluded
    // for the same reason they are for bullets: neither is a thing to look at.
    private bool TryMeasureLookDistance(out float distance)
    {
        distance = 0f;

        if (playerLook == null)
            return false;

        // The range doubles as the ray's length, so anything past it is not measured
        // rather than measured and then discarded.
        if (!Physics.Raycast(playerLook.AimRay, out RaycastHit info, autofocusRange,
                GameLayers.Queryable, QueryTriggerInteraction.Ignore))
            return false;

        distance = Mathf.Max(info.distance, autofocusMinDistance);

        return true;
    }
}

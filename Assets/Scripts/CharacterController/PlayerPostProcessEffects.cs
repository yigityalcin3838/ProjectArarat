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

    [Header("Smoothing")]
    [SerializeField] private float effectSmoothSpeed = 4f;

    private Vignette _vignette;
    private ChromaticAberration _chromaticAberration;
    private DepthOfField _depthOfField;
    private float _baseVignetteIntensity;
    private float _baseVignetteSmoothness;

    private bool _isAiming;

    // Focal length is in millimetres and the other two are far smaller numbers, so
    // this is loose enough for the largest of them and still invisible on it.

    // Lets an equipped item (e.g. Weapon) drive the aim vignette while it's
    // active, without this script needing to know anything about items -- same
    // push-values-in pattern as PlayerLook's FOV override.
    public void SetAiming(bool isAiming) => _isAiming = isAiming;



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

        // Aiming no longer claims the lens -- autofocus keeps it.
        //
        // Which is the better answer for the same moment. A fixed aim lens focuses at
        // a distance decided in the Inspector, so down the sights the sharp plane sat
        // wherever it had been typed regardless of what was being aimed at: correct
        // for a target at that range and wrong at every other. Autofocus focuses on
        // what the crosshair is actually on -- the same ray the round travels -- which
        // is the one thing that is right at any range.
        // One lens, always driving.
        //
        // What used to be here was a priority list -- the sights, then a reload, then
        // autofocus, then the walking carry, then handing the volume its lens back --
        // and every entry on it was a weapon telling the camera how to see. That is
        // backwards: what the lens should be focused on is what is being looked at,
        // and that is true whatever the hands are doing. So the states are gone and
        // the measurement is the whole system.
        //
        // Always on, too. The range gate used to hand focus back to the volume when
        // nothing was close, which meant the effect switched off exactly when the view
        // opened up -- and the handover was itself a change the eye could catch. Out
        // past the range there is simply nothing near enough to blur, so leaving it
        // running costs nothing and never snaps.
        float targetFocusDistance = hasSubject ? measured : autofocusRange;

        // Which way focus is travelling. Pulling in onto something close is decisive;
        // letting out to something further off is not, and the two want different
        // speeds -- see autofocusInSpeed and autofocusOutSpeed.
        float dofSpeed = targetFocusDistance < _depthOfField.focusDistance.value
            ? autofocusInSpeed
            : autofocusOutSpeed;

        float t = dofSpeed * Time.deltaTime;

        _depthOfField.focusDistance.value = Mathf.Lerp(_depthOfField.focusDistance.value, targetFocusDistance, t);
        _depthOfField.aperture.value = Mathf.Lerp(_depthOfField.aperture.value, autofocusAperture, t);
        _depthOfField.focalLength.value = Mathf.Lerp(_depthOfField.focalLength.value, autofocusFocalLength, t);
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
        if (!Physics.Raycast(playerLook.InteractionRay, out RaycastHit info, autofocusRange,
                GameLayers.Queryable, QueryTriggerInteraction.Ignore))
            return false;

        distance = Mathf.Max(info.distance, autofocusMinDistance);

        return true;
    }
}

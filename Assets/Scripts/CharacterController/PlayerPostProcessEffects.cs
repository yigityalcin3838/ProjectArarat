using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PlayerPostProcessEffects : MonoBehaviour
{
    [SerializeField] private PlayerStamina stamina;
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private Volume volume;

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

    [Header("Smoothing")]
    [SerializeField] private float effectSmoothSpeed = 4f;

    private Vignette _vignette;
    private ChromaticAberration _chromaticAberration;
    private DepthOfField _depthOfField;
    private float _baseVignetteIntensity;
    private float _baseVignetteSmoothness;
    private float _baseDofFocusDistance;
    private float _baseDofAperture;
    private float _baseDofFocalLength;
    private bool _isAiming;
    private bool _isReloading;
    private bool _isDrivingDof;

    // Focal length is in millimetres and the other two are far smaller numbers, so
    // this is loose enough for the largest of them and still invisible on it.
    private const float DofSettleThreshold = 0.01f;

    // Lets an equipped item (e.g. Weapon) drive the aim vignette while it's
    // active, without this script needing to know anything about items -- same
    // push-values-in pattern as PlayerLook's FOV override.
    public void SetAiming(bool isAiming) => _isAiming = isAiming;

    public void SetReloading(bool isReloading) => _isReloading = isReloading;

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
        // was authored with is what the effect returns to. Nothing here decides what
        // the world looks like normally; it only decides what a reload does to it.
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

    // Written only while an override is running or on its way out, and left entirely
    // alone otherwise. That is the whole point of the flag: with nothing to say, this
    // stops writing, and the volume's own settings are the ones in effect -- editable
    // in the Inspector, in play mode, without something overwriting them a frame
    // later.
    //
    // While it is quiet it also reads those settings back as the base, so whatever
    // the volume is dialled to is what the next aim or reload returns to. Captured at
    // Awake only, the base would be a snapshot of the moment the game started and
    // every edit afterwards would be undone the first time the sights came up.
    private void UpdateDepthOfField()
    {
        bool wantsOverride = _isReloading || _isAiming;

        if (wantsOverride)
            _isDrivingDof = true;

        if (!_isDrivingDof)
        {
            _baseDofFocusDistance = _depthOfField.focusDistance.value;
            _baseDofAperture = _depthOfField.aperture.value;
            _baseDofFocalLength = _depthOfField.focalLength.value;
            return;
        }

        // Reload first, then aim, then whatever the volume was dialled to. The rate
        // travels with the target rather than being fixed, so each state arrives at
        // its own pace; going back to neutral uses the shared speed every other
        // effect here relaxes on.
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
        else
        {
            targetFocusDistance = _baseDofFocusDistance;
            targetAperture = _baseDofAperture;
            targetFocalLength = _baseDofFocalLength;
            dofSpeed = effectSmoothSpeed;
        }

        float t = dofSpeed * Time.deltaTime;

        _depthOfField.focusDistance.value = Mathf.Lerp(_depthOfField.focusDistance.value, targetFocusDistance, t);
        _depthOfField.aperture.value = Mathf.Lerp(_depthOfField.aperture.value, targetAperture, t);
        _depthOfField.focalLength.value = Mathf.Lerp(_depthOfField.focalLength.value, targetFocalLength, t);

        // Snapped and handed back once the return has effectively finished. A lerp
        // only ever approaches, so without this it would go on writing fractionally
        // different values forever and the volume would never get its settings back.
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
}

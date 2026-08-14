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

    [Header("Smoothing")]
    [SerializeField] private float effectSmoothSpeed = 4f;

    private Vignette _vignette;
    private ChromaticAberration _chromaticAberration;
    private float _baseVignetteIntensity;
    private float _baseVignetteSmoothness;

    private void Awake()
    {
        if (volume != null && volume.profile != null)
        {
            volume.profile.TryGet(out _vignette);
            volume.profile.TryGet(out _chromaticAberration);
        }

        if (_vignette != null)
        {
            _baseVignetteIntensity = _vignette.intensity.value;
            _baseVignetteSmoothness = _vignette.smoothness.value;
        }
    }

    private void Update()
    {
        if (_vignette == null && _chromaticAberration == null)
            return;

        float staminaAmount = 0f;
        if (stamina != null)
        {
            float threshold = stamina.MaxStamina * lowStaminaThresholdRatio;
            staminaAmount = threshold > 0f ? Mathf.Clamp01(1f - stamina.CurrentStamina / threshold) : 0f;
        }

        float crouchAmount = movement != null && movement.IsCrouching ? 1f : 0f;

        if (_vignette != null)
        {
            float targetIntensity = _baseVignetteIntensity + Mathf.Max(staminaAmount * lowStaminaVignetteIntensity, crouchAmount * crouchVignetteIntensity);
            float targetSmoothness = _baseVignetteSmoothness + Mathf.Max(staminaAmount * lowStaminaVignetteSmoothness, crouchAmount * crouchVignetteSmoothness);

            _vignette.intensity.value = Mathf.Lerp(_vignette.intensity.value, targetIntensity, effectSmoothSpeed * Time.deltaTime);
            _vignette.smoothness.value = Mathf.Lerp(_vignette.smoothness.value, targetSmoothness, effectSmoothSpeed * Time.deltaTime);
        }

        if (_chromaticAberration != null)
        {
            float targetChromatic = staminaAmount * lowStaminaChromaticAberration;
            _chromaticAberration.intensity.value = Mathf.Lerp(_chromaticAberration.intensity.value, targetChromatic, effectSmoothSpeed * Time.deltaTime);
        }
    }
}

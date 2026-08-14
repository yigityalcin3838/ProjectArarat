using UnityEngine;
using TMPro;

public class PlayerStamina : MonoBehaviour
{
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private TMP_Text staminaText;

    [Header("Stamina")]
    [SerializeField] private float maxStamina = 100f;
    [SerializeField] private float sprintDrainRate = 10f;
    [SerializeField] private float jumpStaminaCost = 15f;
    [SerializeField] private float staminaRegenRate = 8f;
    [SerializeField] private float regenDelay = 1.5f;

    public float MaxStamina => maxStamina;
    public float CurrentStamina { get; private set; }
    public bool HasEnoughForJump => CurrentStamina >= jumpStaminaCost;

    private float _lastDrainTime;

    private void Awake()
    {
        CurrentStamina = maxStamina;
    }

    private void Update()
    {
        if (movement.IsSprinting)
            Drain(sprintDrainRate * Time.deltaTime);
        else if (Time.time - _lastDrainTime >= regenDelay)
            Regen(staminaRegenRate * Time.deltaTime);

        if (staminaText != null)
            staminaText.text = $"{Mathf.CeilToInt(CurrentStamina)}/{Mathf.CeilToInt(maxStamina)}";
    }

    public void ConsumeJumpStamina() => Drain(jumpStaminaCost);

    private void Drain(float amount)
    {
        CurrentStamina = Mathf.Max(0f, CurrentStamina - amount);
        _lastDrainTime = Time.time;
    }

    private void Regen(float amount)
    {
        CurrentStamina = Mathf.Min(maxStamina, CurrentStamina + amount);
    }
}

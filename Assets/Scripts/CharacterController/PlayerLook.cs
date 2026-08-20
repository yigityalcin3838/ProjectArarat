using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerLook : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private InputActionAsset inputActions;

    [Header("Look")]
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private float mouseSensitivity = 0.1f;
    [SerializeField] private float maxLookAngle = 85f;

    [Header("Strafe Tilt")]
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private float tiltAmount = 3f;
    [SerializeField] private float tiltSpeed = 8f;

    [Header("Speed FOV")]
    [SerializeField] private CinemachineCamera cinemachineCamera;
    [SerializeField] private float maxFovBoost = 10f;
    [SerializeField] private float fovSpeed = 8f;

    [Header("Ladder Look")]
    [SerializeField] private float climbLookYawLimit = 100f;
    [SerializeField] private float climbLookPitchUpLimit = 60f;
    [SerializeField] private float climbLookPitchDownLimit = 60f;

    [Header("Car Look")]
    [SerializeField] private float carLookYawLimitRight = 60f;
    [SerializeField] private float carLookYawLimitLeft = 60f;
    [SerializeField] private float carLookPitchUpLimit = 30f;
    [SerializeField] private float carLookPitchDownLimit = 30f;
    [SerializeField, Range(0f, 1f)] private float carPitchLimitAtMaxYawRatio = 0.2f;

    [Header("Peek Tilt")]
    [SerializeField] private float peekTiltAmount = 10f;

    private const float DefaultKickSpring = 200f;
    private const float DefaultKickDamping = 20f;

    public Transform CameraTransform => cameraTransform;
    public float Pitch { get; private set; }
    public float YawDelta { get; private set; }

    private InputAction _lookAction;
    private Vector2 _lookInput;
    private float _currentTilt;
    private float _baseFov;
    private float _climbCameraYaw;
    private bool _wasClimbing;
    private float? _fovOverride;
    private float _rollKickOffset;
    private float _rollKickVelocity;
    private float _pitchKickOffset;
    private float _pitchKickVelocity;
    private float? _kickSpringOverride;
    private float? _kickDampingOverride;

    // Lets an equipped item (e.g. Pistol) override FOV while it's active,
    // without PlayerLook needing to know anything about items -- same
    // push-values-in pattern as PlayerAnimator's hand IK targets.
    public void SetFovOverride(float fov) => _fovOverride = fov;
    public void ClearFovOverride() => _fovOverride = null;

    // Lets an equipped item (e.g. Pistol) kick the camera's roll/pitch on an event
    // (e.g. firing) -- same push-values-in pattern, a damped spring absorbs it.
    // The spring/damping constants themselves are also pushed in by the item
    // (SetCameraKickProfile) so its feel can be tuned from the item's own code.
    public void AddRollKickImpulse(float amount) => _rollKickVelocity += amount;
    public void AddPitchKickImpulse(float amount) => _pitchKickVelocity -= amount; // positive = kicks the camera up

    public void SetCameraKickProfile(float spring, float damping)
    {
        _kickSpringOverride = spring;
        _kickDampingOverride = damping;
    }

    public void ClearCameraKickProfile()
    {
        _kickSpringOverride = null;
        _kickDampingOverride = null;
    }

    private void Awake()
    {
        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        _lookAction = playerMap.FindAction("Look");

        if (cinemachineCamera != null)
            _baseFov = cinemachineCamera.Lens.FieldOfView;
    }

    private void OnEnable()
    {
        _lookAction.Enable();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void OnDisable()
    {
        _lookAction.Disable();
    }

    private void Update()
    {
        _lookInput = _lookAction.ReadValue<Vector2>();
        ApplyLook();
    }

    private void ApplyLook()
    {
        bool isClimbing = movement != null && movement.IsClimbingLadder;
        bool isInCar = movement != null && movement.IsInCar;
        bool lockBodyYaw = isClimbing || isInCar;

        if (_wasClimbing && !lockBodyYaw)
        {
            transform.Rotate(Vector3.up * _climbCameraYaw);
            _climbCameraYaw = 0f;
        }
        _wasClimbing = lockBodyYaw;

        float yaw = _lookInput.x * mouseSensitivity;

        if (lockBodyYaw)
        {
            float yawLimitLeft = isInCar ? carLookYawLimitLeft : climbLookYawLimit;
            float yawLimitRight = isInCar ? carLookYawLimitRight : climbLookYawLimit;
            _climbCameraYaw = Mathf.Clamp(_climbCameraYaw + yaw, -yawLimitLeft, yawLimitRight);
            YawDelta = 0f;
        }
        else
        {
            transform.Rotate(Vector3.up * yaw);
            YawDelta = yaw;
        }

        float pitchUpLimit = isInCar ? carLookPitchUpLimit : (isClimbing ? climbLookPitchUpLimit : maxLookAngle);
        float pitchDownLimit = isInCar ? carLookPitchDownLimit : (isClimbing ? climbLookPitchDownLimit : maxLookAngle);

        // The further the camera has turned toward its yaw limit in the car, the less
        // pitch freedom it has left -- mirrors how far you can actually tilt your head
        // up/down once you've already twisted your neck near its rotational limit.
        if (isInCar)
        {
            float yawLimitForRatio = _climbCameraYaw >= 0f ? carLookYawLimitRight : carLookYawLimitLeft;
            float yawRatio = yawLimitForRatio > 0f ? Mathf.Clamp01(Mathf.Abs(_climbCameraYaw) / yawLimitForRatio) : 0f;
            float pitchScale = Mathf.Lerp(1f, carPitchLimitAtMaxYawRatio, yawRatio);
            pitchUpLimit *= pitchScale;
            pitchDownLimit *= pitchScale;
        }

        Pitch = Mathf.Clamp(Pitch - _lookInput.y * mouseSensitivity, -pitchUpLimit, pitchDownLimit);

        float targetTilt = movement != null && !movement.IsMovementLocked
            ? (-movement.MoveInput.x * tiltAmount) + (-movement.PeekAmount * peekTiltAmount)
            : 0f;
        _currentTilt = Mathf.Lerp(_currentTilt, targetTilt, tiltSpeed * Time.deltaTime);

        // Damped spring pulling the kick offsets back to 0 -- an impulse on the
        // velocity makes them snap away and settle back, instead of a plain lerp
        // which can't overshoot/oscillate.
        float kickSpring = _kickSpringOverride ?? DefaultKickSpring;
        float kickDamping = _kickDampingOverride ?? DefaultKickDamping;

        _rollKickVelocity += (-kickSpring * _rollKickOffset - kickDamping * _rollKickVelocity) * Time.deltaTime;
        _rollKickOffset += _rollKickVelocity * Time.deltaTime;

        _pitchKickVelocity += (-kickSpring * _pitchKickOffset - kickDamping * _pitchKickVelocity) * Time.deltaTime;
        _pitchKickOffset += _pitchKickVelocity * Time.deltaTime;

        if (cameraTransform != null)
            cameraTransform.localRotation = Quaternion.Euler(Pitch + _pitchKickOffset, _climbCameraYaw, _currentTilt + _rollKickOffset);

        if (cinemachineCamera != null)
        {
            float fovBoostRatio = 0f;
            if (movement != null)
            {
                if (movement.IsSprintingStable)
                    fovBoostRatio = 1f;
                else if (isInCar)
                    fovBoostRatio = movement.CarSpeedRatio;
            }

            LensSettings lens = cinemachineCamera.Lens;
            float targetFov = _fovOverride ?? (_baseFov + maxFovBoost * fovBoostRatio);
            lens.FieldOfView = Mathf.Lerp(lens.FieldOfView, targetFov, fovSpeed * Time.deltaTime);
            cinemachineCamera.Lens = lens;
        }
    }
}

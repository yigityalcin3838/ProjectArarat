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

    [Header("Camera Breathing")]
    [SerializeField] private float breathFrequency = 1f;
    [SerializeField] private float breathPitchAmount = 0.3f;
    [SerializeField] private float breathYawAmount = 0.3f;
    [SerializeField] private float breathRollAmount = 0.3f;
    [SerializeField] private float breathSmoothing = 4f;

    [Header("Camera Bob")]
    [SerializeField] private float bobFrequency = 6f;
    [SerializeField] private float bobPitchAmount = 0.5f;
    [SerializeField] private float bobYawAmount = 0.5f;
    [SerializeField] private float bobRollAmount = 0.5f;
    [SerializeField] private float crouchBobMultiplier = 0.5f;
    [SerializeField] private float sprintBobMultiplier = 1.5f;
    [SerializeField] private float bobSmoothing = 8f;

    [Header("Camera Jump / Land Shake")]
    [SerializeField] private float jumpShakeAmount = 2f;
    [SerializeField] private float landShakeAmount = 4f;
    [SerializeField] private float shakeSpring = 200f;
    [SerializeField] private float shakeDamping = 20f;

    public Transform CameraTransform => cameraTransform;
    public float Pitch { get; private set; }
    public float YawDelta { get; private set; }

    // The camera bob's own sine phase, including whatever crouch/sprint
    // rate scaling it's currently applying -- exposed so the weapon's hand
    // bob (Pistol) can drive its own bob off the exact same phase instead of
    // running an independent timer, which is what keeps the two bobs
    // genuinely in sync (matching frequencies alone isn't enough: two
    // separate timers can still drift apart frame to frame).
    public float BobPhase => _bobTimer;

    private InputAction _lookAction;
    private Vector2 _lookInput;
    private float _currentTilt;
    private float _baseFov;
    private float _climbCameraYaw;
    private bool _wasClimbing;
    private float? _fovOverride;
    private float _breathTimer;
    private Vector3 _currentBreathRotation;
    private float _bobTimer;
    private Vector3 _currentBobRotation;
    private float _shakeOffset;
    private float _shakeVelocity;
    private float _fireKickOffset;
    private float _fireKickVelocity;
    private float _fireKickYawOffset;
    private float _fireKickYawVelocity;
    private float _fireKickSpring = 200f;
    private float _fireKickDamping = 20f;
    private float _rollShakeOffset;
    private float _rollShakeVelocity;
    private float _rollShakeSpring = 200f;
    private float _rollShakeDamping = 20f;

    // Lets an equipped item (e.g. Pistol) override FOV while it's active,
    // without PlayerLook needing to know anything about items -- same
    // push-values-in pattern as PlayerAnimator's hand IK targets.
    public void SetFovOverride(float fov) => _fovOverride = fov;
    public void ClearFovOverride() => _fovOverride = null;

    // Weapon-driven recoil kick on the camera itself -- the weapon owns the
    // amount/spring/damping (its recoil "feel") and just pushes them in,
    // same push-values-in pattern as the FOV override above.
    public void SetFireKickProfile(float spring, float damping)
    {
        _fireKickSpring = spring;
        _fireKickDamping = damping;
    }

    // Roll shake has its own spring/damping, independent from the pitch/yaw
    // kick above -- a cosmetic rattle on top of the deterministic punch.
    public void SetRollShakeProfile(float spring, float damping)
    {
        _rollShakeSpring = spring;
        _rollShakeDamping = damping;
    }

    // Modern CoD-style recoil kick: a deterministic upward pitch punch plus a
    // random left/right yaw punch per shot, plus a random roll shake with its
    // own spring/damping -- all settle back independently. Velocities are
    // set, not added, so rapid fire can't stack shots into a runaway kick.
    public void AddFireKick(float kickAmount, float horizontalKickAmount, float rollShakeAmount)
    {
        _fireKickVelocity = -kickAmount;
        _fireKickYawVelocity = Random.Range(-1f, 1f) * horizontalKickAmount;
        _rollShakeVelocity = Random.Range(-1f, 1f) * rollShakeAmount;
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

        bool isMoving = movement != null && movement.IsGroundedStable && !movement.IsMovementLocked && movement.MoveInput.sqrMagnitude > 0.01f;

        // Purely rotational -- a slow sine drift on pitch/yaw/roll, no position
        // offset, so the camera never feels perfectly locked while standing
        // still. Only while standing still, though -- bob (below) takes over
        // once moving instead of the two stacking.
        if (!isMoving)
            _breathTimer += Time.deltaTime * breathFrequency;

        Vector3 targetBreathRotation = !isMoving
            ? new Vector3(
                Mathf.Sin(_breathTimer * 0.5f) * breathPitchAmount,
                Mathf.Sin(_breathTimer) * breathYawAmount,
                Mathf.Cos(_breathTimer) * breathRollAmount)
            : Vector3.zero;
        _currentBreathRotation = Vector3.Lerp(_currentBreathRotation, targetBreathRotation, breathSmoothing * Time.deltaTime);

        // Also purely rotational -- speeds up and grows with movement state
        // (crouch slower/smaller, sprint faster/bigger). Pitch on the doubled
        // frequency; yaw and roll on the base frequency, 90 degrees apart from
        // each other for a natural circular swing (same pairing as the weapon's
        // own hand bob).
        float bobStateMultiplier = movement != null && movement.IsCrouching ? crouchBobMultiplier
            : movement != null && movement.IsSprintingStable ? sprintBobMultiplier
            : 1f;

        if (isMoving)
            _bobTimer += Time.deltaTime * bobFrequency * bobStateMultiplier;

        Vector3 targetBobRotation = isMoving
            ? new Vector3(
                Mathf.Sin(_bobTimer * 2f) * bobPitchAmount * bobStateMultiplier,
                Mathf.Sin(_bobTimer) * bobYawAmount * bobStateMultiplier,
                Mathf.Cos(_bobTimer) * bobRollAmount * bobStateMultiplier)
            : Vector3.zero;
        _currentBobRotation = Vector3.Lerp(_currentBobRotation, targetBobRotation, bobSmoothing * Time.deltaTime);

        // Pitch-only damped spring kick on jump (up) and landing (down) -- an
        // impulse on velocity snaps it away and settles back like a real spring,
        // same pattern as the weapon's own jump/land kick.
        if (movement != null && movement.JumpedThisFrame)
            _shakeVelocity -= jumpShakeAmount;
        if (movement != null && movement.LandedThisFrame)
            _shakeVelocity += landShakeAmount;

        _shakeVelocity += (-shakeSpring * _shakeOffset - shakeDamping * _shakeVelocity) * Time.deltaTime;
        _shakeOffset += _shakeVelocity * Time.deltaTime;

        // Weapon-driven recoil kick -- spring/damping come from whatever item is
        // equipped (pushed via SetFireKickProfile), impulse from AddFireKick per shot.
        _fireKickVelocity += (-_fireKickSpring * _fireKickOffset - _fireKickDamping * _fireKickVelocity) * Time.deltaTime;
        _fireKickOffset += _fireKickVelocity * Time.deltaTime;

        _fireKickYawVelocity += (-_fireKickSpring * _fireKickYawOffset - _fireKickDamping * _fireKickYawVelocity) * Time.deltaTime;
        _fireKickYawOffset += _fireKickYawVelocity * Time.deltaTime;

        _rollShakeVelocity += (-_rollShakeSpring * _rollShakeOffset - _rollShakeDamping * _rollShakeVelocity) * Time.deltaTime;
        _rollShakeOffset += _rollShakeVelocity * Time.deltaTime;

        if (cameraTransform != null)
        {
            cameraTransform.localRotation = Quaternion.Euler(
                Pitch + _currentBreathRotation.x + _currentBobRotation.x + _shakeOffset + _fireKickOffset,
                _climbCameraYaw + _currentBreathRotation.y + _currentBobRotation.y + _fireKickYawOffset,
                _currentTilt + _currentBreathRotation.z + _currentBobRotation.z + _rollShakeOffset);
        }

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

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
    [SerializeField] private float carLookYawLimit = 60f;
    [SerializeField] private float carLookPitchUpLimit = 30f;
    [SerializeField] private float carLookPitchDownLimit = 30f;

    [Header("Peek Tilt")]
    [SerializeField] private float peekTiltAmount = 10f;

    public Transform CameraTransform => cameraTransform;
    public float Pitch { get; private set; }
    public float YawDelta { get; private set; }

    private InputAction _lookAction;
    private Vector2 _lookInput;
    private float _currentTilt;
    private float _baseFov;
    private float _climbCameraYaw;
    private bool _wasClimbing;

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

        float pitchUpLimit = isInCar ? carLookPitchUpLimit : (isClimbing ? climbLookPitchUpLimit : maxLookAngle);
        float pitchDownLimit = isInCar ? carLookPitchDownLimit : (isClimbing ? climbLookPitchDownLimit : maxLookAngle);
        Pitch = Mathf.Clamp(Pitch - _lookInput.y * mouseSensitivity, -pitchUpLimit, pitchDownLimit);

        if (lockBodyYaw)
        {
            float yawLimit = isInCar ? carLookYawLimit : climbLookYawLimit;
            _climbCameraYaw = Mathf.Clamp(_climbCameraYaw + yaw, -yawLimit, yawLimit);
            YawDelta = 0f;
        }
        else
        {
            transform.Rotate(Vector3.up * yaw);
            YawDelta = yaw;
        }

        float targetTilt = movement != null && !movement.IsMovementLocked
            ? (-movement.MoveInput.x * tiltAmount) + (-movement.PeekAmount * peekTiltAmount)
            : 0f;
        _currentTilt = Mathf.Lerp(_currentTilt, targetTilt, tiltSpeed * Time.deltaTime);

        if (cameraTransform != null)
            cameraTransform.localRotation = Quaternion.Euler(Pitch, _climbCameraYaw, _currentTilt);

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
            float targetFov = _baseFov + maxFovBoost * fovBoostRatio;
            lens.FieldOfView = Mathf.Lerp(lens.FieldOfView, targetFov, fovSpeed * Time.deltaTime);
            cinemachineCamera.Lens = lens;
        }
    }
}

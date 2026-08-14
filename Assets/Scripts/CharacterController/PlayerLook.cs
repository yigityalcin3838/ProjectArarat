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

    [Header("Sprint FOV")]
    [SerializeField] private CinemachineCamera cinemachineCamera;
    [SerializeField] private float sprintFovBoost = 10f;
    [SerializeField] private float fovSpeed = 8f;

    [Header("Ladder Look")]
    [SerializeField] private float climbLookYawLimit = 100f;

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
        bool lockBodyYaw = movement != null && (movement.IsClimbingLadder || movement.IsInCar);

        if (_wasClimbing && !lockBodyYaw)
        {
            transform.Rotate(Vector3.up * _climbCameraYaw);
            _climbCameraYaw = 0f;
        }
        _wasClimbing = lockBodyYaw;

        float yaw = _lookInput.x * mouseSensitivity;
        Pitch = Mathf.Clamp(Pitch - _lookInput.y * mouseSensitivity, -maxLookAngle, maxLookAngle);

        if (lockBodyYaw)
        {
            _climbCameraYaw = Mathf.Clamp(_climbCameraYaw + yaw, -climbLookYawLimit, climbLookYawLimit);
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
            LensSettings lens = cinemachineCamera.Lens;
            float targetFov = _baseFov + (movement != null && movement.IsSprintingStable ? sprintFovBoost : 0f);
            lens.FieldOfView = Mathf.Lerp(lens.FieldOfView, targetFov, fovSpeed * Time.deltaTime);
            cinemachineCamera.Lens = lens;
        }
    }
}

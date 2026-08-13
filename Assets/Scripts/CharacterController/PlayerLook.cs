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
    [SerializeField] private Camera playerCamera;
    [SerializeField] private float sprintFovBoost = 10f;
    [SerializeField] private float fovSpeed = 8f;

    public Transform CameraTransform => cameraTransform;
    public float Pitch { get; private set; }
    public float YawDelta { get; private set; }

    private InputAction _lookAction;
    private Vector2 _lookInput;
    private float _currentTilt;
    private float _baseFov;

    private void Awake()
    {
        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        _lookAction = playerMap.FindAction("Look");

        if (playerCamera == null && cameraTransform != null)
            playerCamera = cameraTransform.GetComponent<Camera>();

        if (playerCamera != null)
            _baseFov = playerCamera.fieldOfView;
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
        float yaw = _lookInput.x * mouseSensitivity;
        Pitch = Mathf.Clamp(Pitch - _lookInput.y * mouseSensitivity, -maxLookAngle, maxLookAngle);

        transform.Rotate(Vector3.up * yaw);
        YawDelta = yaw;

        float targetTilt = movement != null ? -movement.MoveInput.x * tiltAmount : 0f;
        _currentTilt = Mathf.Lerp(_currentTilt, targetTilt, tiltSpeed * Time.deltaTime);

        if (cameraTransform != null)
            cameraTransform.localRotation = Quaternion.Euler(Pitch, 0f, _currentTilt);

        if (playerCamera != null)
        {
            float targetFov = _baseFov + (movement != null && movement.IsSprinting ? sprintFovBoost : 0f);
            playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, targetFov, fovSpeed * Time.deltaTime);
        }
    }
}

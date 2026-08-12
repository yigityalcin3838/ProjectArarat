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

    public Transform CameraTransform => cameraTransform;
    public float Pitch { get; private set; }

    private InputAction _lookAction;
    private Vector2 _lookInput;

    private void Awake()
    {
        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        _lookAction = playerMap.FindAction("Look");
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
        if (cameraTransform != null)
            cameraTransform.localRotation = Quaternion.Euler(Pitch, 0f, 0f);
    }
}

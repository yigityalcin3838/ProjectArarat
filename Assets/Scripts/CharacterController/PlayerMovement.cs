using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(CapsuleCollider))]
public class PlayerMovement : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private InputActionAsset inputActions;

    [Header("Movement")]
    [SerializeField] private float walkSpeed = 5f;
    [SerializeField] private float sprintSpeed = 8f;
    [SerializeField] private float jumpForce = 6f;

    [Header("Drag & Air Control")]
    [SerializeField] private float groundDrag = 6f;
    [SerializeField] private float airMultiplier = 0.4f;

    [Header("Ground Check")]
    [SerializeField] private float groundCheckDistance = 0.15f;

    public bool IsGrounded { get; private set; }
    public Vector3 Velocity => _rb.linearVelocity;
    public Vector2 MoveInput => _moveInput;
    public float WalkSpeed => walkSpeed;
    public float SprintSpeed => sprintSpeed;
    public bool IsSprinting => _sprintAction.IsPressed();

    private Rigidbody _rb;
    private CapsuleCollider _capsule;
    private InputAction _moveAction;
    private InputAction _jumpAction;
    private InputAction _sprintAction;

    private Vector2 _moveInput;
    private bool _jumpQueued;
    private float _nextSpeedLogTime;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.freezeRotation = true;
        _capsule = GetComponent<CapsuleCollider>();

        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        _moveAction = playerMap.FindAction("Move");
        _jumpAction = playerMap.FindAction("Jump");
        _sprintAction = playerMap.FindAction("Sprint");
    }

    private void OnEnable()
    {
        _moveAction.Enable();
        _jumpAction.Enable();
        _sprintAction.Enable();
    }

    private void OnDisable()
    {
        _moveAction.Disable();
        _jumpAction.Disable();
        _sprintAction.Disable();
    }

    private void Update()
    {
        _moveInput = _moveAction.ReadValue<Vector2>();

        if (_jumpAction.WasPerformedThisFrame() && IsGrounded)
            _jumpQueued = true;

        if (Time.time >= _nextSpeedLogTime)
        {
            Vector3 horizontalVelocity = new Vector3(Velocity.x, 0f, Velocity.z);
            Debug.Log($"Speed: {horizontalVelocity.magnitude:F2} m/s");
            _nextSpeedLogTime = Time.time + 1f;
        }
    }

    private void FixedUpdate()
    {
        CheckGrounded();
        ApplyMovement();

        if (_jumpQueued)
        {
            _rb.linearVelocity = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
            _rb.AddForce(Vector3.up * jumpForce, ForceMode.VelocityChange);
            _jumpQueued = false;
        }
    }

    private void CheckGrounded()
    {
        float radius = _capsule.radius * 0.9f;
        Vector3 origin = transform.position + Vector3.up * (radius + 0.05f);
        IsGrounded = Physics.SphereCast(origin, radius, Vector3.down, out _, groundCheckDistance + 0.05f);
    }

    private void ApplyMovement()
    {
        Vector3 wishDir = transform.right * _moveInput.x + transform.forward * _moveInput.y;
        wishDir = Vector3.ClampMagnitude(wishDir, 1f);

        float currentSpeed = IsSprinting ? sprintSpeed : walkSpeed;

        float forceMultiplier = IsGrounded ? 1f : airMultiplier;
        _rb.AddForce(wishDir * currentSpeed * 10f * forceMultiplier, ForceMode.Force);

        _rb.linearDamping = IsGrounded ? groundDrag : 0f;

        ClampHorizontalSpeed(currentSpeed);
    }

    private void ClampHorizontalSpeed(float maxSpeed)
    {
        Vector3 velocity = _rb.linearVelocity;
        Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);

        if (horizontal.magnitude > maxSpeed)
        {
            Vector3 limited = horizontal.normalized * maxSpeed;
            _rb.linearVelocity = new Vector3(limited.x, velocity.y, limited.z);
        }
    }
}

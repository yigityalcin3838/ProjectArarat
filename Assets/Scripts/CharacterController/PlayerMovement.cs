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
    [SerializeField] private float maxSlopeAngle = 45f;
    [SerializeField] private float airborneGraceTime = 0.15f;

    [Header("Crouch")]
    [SerializeField] private float crouchSpeed = 2.5f;
    [SerializeField] private float standingHeight = 1.8f;
    [SerializeField] private float crouchHeight = 1f;

    [Header("Step Climb")]
    [SerializeField] private float stepHeight = 0.3f;
    [SerializeField] private float stepClimbDuration = 0.12f;

    public bool IsGrounded { get; private set; }
    public bool IsGroundedStable => IsGrounded || (Time.time - _lastGroundedTime) < airborneGraceTime;
    public bool IsCrouching { get; private set; }
    public Vector3 Velocity => _rb.linearVelocity;
    public Vector2 MoveInput => _moveInput;
    public float WalkSpeed => walkSpeed;
    public float SprintSpeed => sprintSpeed;
    public bool IsSprinting => IsGrounded && !IsCrouching && _sprintAction.IsPressed();

    private Rigidbody _rb;
    private CapsuleCollider _capsule;
    private InputAction _moveAction;
    private InputAction _jumpAction;
    private InputAction _sprintAction;
    private InputAction _crouchAction;

    private Vector2 _moveInput;
    private bool _jumpQueued;
    private float _nextSpeedLogTime;
    private float _lastGroundedTime;
    private Vector3 _groundNormal = Vector3.up;
    private bool _isClimbingStep;
    private Vector3 _stepClimbStart;
    private Vector3 _stepClimbTarget;
    private float _stepClimbT;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.freezeRotation = true;
        _capsule = GetComponent<CapsuleCollider>();

        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        _moveAction = playerMap.FindAction("Move");
        _jumpAction = playerMap.FindAction("Jump");
        _sprintAction = playerMap.FindAction("Sprint");
        _crouchAction = playerMap.FindAction("Crouch");
    }

    private void OnEnable()
    {
        _moveAction.Enable();
        _jumpAction.Enable();
        _sprintAction.Enable();
        _crouchAction.Enable();
    }

    private void OnDisable()
    {
        _moveAction.Disable();
        _jumpAction.Disable();
        _sprintAction.Disable();
        _crouchAction.Disable();
    }

    private void Update()
    {
        _moveInput = _moveAction.ReadValue<Vector2>();

        if (_jumpAction.WasPerformedThisFrame() && IsGrounded && !IsCrouching)
            _jumpQueued = true;

        UpdateCrouch();

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

        if (_isClimbingStep)
            UpdateStepClimb();
        else
            HandleStepClimb();

        ApplyMovement();

        if (_jumpQueued)
        {
            _rb.linearVelocity = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
            _rb.AddForce(Vector3.up * jumpForce, ForceMode.VelocityChange);
            _jumpQueued = false;
        }
    }

    private void UpdateCrouch()
    {
        bool wantsCrouch = _crouchAction.IsPressed();
        bool blockedFromStanding = IsCrouching && !wantsCrouch && !HasHeadroomToStand();

        IsCrouching = IsGroundedStable && (wantsCrouch || blockedFromStanding);

        _capsule.height = IsCrouching ? crouchHeight : standingHeight;
        _capsule.center = new Vector3(0f, _capsule.height * 0.5f, 0f);
    }

    private bool HasHeadroomToStand()
    {
        float radius = _capsule.radius * 0.95f;
        float clearanceNeeded = standingHeight - crouchHeight;
        Vector3 origin = transform.position + Vector3.up * (crouchHeight - radius);

        return !Physics.SphereCast(origin, radius, Vector3.up, out _, clearanceNeeded + radius);
    }

    private void CheckGrounded()
    {
        float radius = _capsule.radius * 0.9f;
        Vector3 origin = transform.position + Vector3.up * (radius + 0.05f);

        bool hitGround = Physics.SphereCast(origin, radius, Vector3.down, out RaycastHit hit, groundCheckDistance + 0.05f);
        IsGrounded = hitGround && Vector3.Angle(hit.normal, Vector3.up) <= maxSlopeAngle;
        _groundNormal = hitGround ? hit.normal : Vector3.up;

        if (IsGrounded)
            _lastGroundedTime = Time.time;
    }

    private void HandleStepClimb()
    {
        if (!IsGrounded)
            return;

        Vector3 moveDir = transform.right * _moveInput.x + transform.forward * _moveInput.y;
        if (moveDir.sqrMagnitude < 0.01f)
            return;
        moveDir.Normalize();

        float castDistance = _capsule.radius + 0.2f;
        Vector3 lowerOrigin = transform.position + Vector3.up * 0.05f;
        Vector3 upperOrigin = transform.position + Vector3.up * stepHeight;

        bool hitLower = Physics.Raycast(lowerOrigin, moveDir, out RaycastHit lowerHit, castDistance);
        bool hitUpper = Physics.Raycast(upperOrigin, moveDir, castDistance);

        if (!hitLower || hitUpper)
            return;

        bool isWalkableSlope = Vector3.Angle(lowerHit.normal, Vector3.up) <= maxSlopeAngle;
        if (isWalkableSlope)
            return;

        Vector3 probeOrigin = lowerHit.point + moveDir * 0.15f + Vector3.up * (stepHeight + 0.1f);

        if (!Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit topHit, stepHeight + 0.2f))
            return;

        if (topHit.point.y <= transform.position.y + 0.01f)
            return;

        _isClimbingStep = true;
        _stepClimbStart = transform.position;
        _stepClimbTarget = new Vector3(probeOrigin.x, topHit.point.y + 0.02f, probeOrigin.z);
        _stepClimbT = 0f;
    }

    private void UpdateStepClimb()
    {
        _stepClimbT += Time.fixedDeltaTime / stepClimbDuration;

        if (_stepClimbT >= 1f)
        {
            _rb.MovePosition(_stepClimbTarget);
            _rb.linearVelocity = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
            _isClimbingStep = false;
            return;
        }

        _rb.MovePosition(Vector3.Lerp(_stepClimbStart, _stepClimbTarget, _stepClimbT));
    }

    private void ApplyMovement()
    {
        Vector3 wishDir = transform.right * _moveInput.x + transform.forward * _moveInput.y;
        wishDir = Vector3.ClampMagnitude(wishDir, 1f);

        float currentSpeed = IsCrouching ? crouchSpeed : (IsSprinting ? sprintSpeed : walkSpeed);

        float forceMultiplier = IsGrounded ? 1f : airMultiplier;
        _rb.AddForce(wishDir * currentSpeed * 10f * forceMultiplier, ForceMode.Force);

        _rb.linearDamping = IsGrounded ? groundDrag : 0f;

        if (IsGrounded)
            CancelSlopeSlide();

        ClampHorizontalSpeed(currentSpeed);
    }

    private void CancelSlopeSlide()
    {
        Vector3 tangentialGravity = Physics.gravity - Vector3.Project(Physics.gravity, _groundNormal);
        _rb.AddForce(-tangentialGravity, ForceMode.Acceleration);
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

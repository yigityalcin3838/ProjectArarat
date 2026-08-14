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
    [SerializeField] private float stepClimbDurationWalk = 0.12f;
    [SerializeField] private float stepClimbDurationSprint = 0.12f;
    [SerializeField] private float stepClimbDurationCrouch = 0.12f;

    [Header("Animator Link")]
    [SerializeField] private PlayerAnimator playerAnimator;
    [SerializeField] private PlayerStamina stamina;

    [Header("Ladder")]
    [SerializeField] private string ladderTag = "Ladder";
    [SerializeField] private float ladderInteractDistance = 1.5f;
    [SerializeField] private float ladderClimbSpeed = 3f;
    [SerializeField] private float ladderSnapSpeed = 10f;
    [SerializeField] private float ladderEnterDuration = 0.4f;
    [SerializeField] private float ladderJumpOffForce = 4f;

    [Header("Door")]
    [SerializeField] private string doorTag = "Door";
    [SerializeField] private float doorInteractDistance = 2f;

    public float PeekAmount { get; private set; }

    public bool IsGrounded { get; private set; }
    public bool IsGroundedStable => IsGrounded || (Time.time - _lastGroundedTime) < airborneGraceTime;
    public bool IsCrouching { get; private set; }
    public Vector3 Velocity => _rb.linearVelocity;
    public Vector2 MoveInput => _moveInput;
    public float WalkSpeed => walkSpeed;
    public float SprintSpeed => sprintSpeed;
    public bool IsSprinting => IsGrounded && !IsCrouching && _sprintAction.IsPressed() && _moveInput.sqrMagnitude > 0.01f && HasStamina;
    public bool IsSprintingStable => IsGroundedStable && !IsCrouching && _sprintAction.IsPressed() && _moveInput.sqrMagnitude > 0.01f && HasStamina;
    public bool IsClimbingLadder { get; private set; }

    private bool HasStamina => stamina == null || stamina.CurrentStamina > 0f;
    private bool CanJump => stamina == null || stamina.HasEnoughForJump;

    private Rigidbody _rb;
    private CapsuleCollider _capsule;
    private InputAction _moveAction;
    private InputAction _jumpAction;
    private InputAction _sprintAction;
    private InputAction _crouchAction;
    private InputAction _interactAction;
    private InputAction _peekAction;

    private Ladder _activeLadder;
    private bool _isEnteringLadder;
    private bool _isEnteringFromTop;
    private Vector3 _ladderEnterStart;
    private Quaternion _ladderEnterStartRotation;
    private float _ladderEnterT;
    private bool _isPlayingLadderTransition;
    private bool _ladderTransitionReversed;

    private Vector2 _moveInput;
    private bool _jumpQueued;
    private float _lastGroundedTime;
    private Vector3 _groundNormal = Vector3.up;
    private bool _isClimbingStep;
    private Vector3 _stepClimbStart;
    private Vector3 _stepClimbTarget;
    private float _stepClimbT;
    private float _stepClimbDurationActual;

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
        _interactAction = playerMap.FindAction("Interact");
        _peekAction = playerMap.FindAction("Peek");
    }

    private void OnEnable()
    {
        _moveAction.Enable();
        _jumpAction.Enable();
        _sprintAction.Enable();
        _crouchAction.Enable();
        _interactAction.Enable();
        _peekAction.Enable();
    }

    private void OnDisable()
    {
        _moveAction.Disable();
        _jumpAction.Disable();
        _sprintAction.Disable();
        _crouchAction.Disable();
        _interactAction.Disable();
        _peekAction.Disable();
    }

    private void Update()
    {
        _moveInput = _moveAction.ReadValue<Vector2>();

        if (_jumpAction.WasPerformedThisFrame())
        {
            if (IsClimbingLadder && !_isEnteringLadder && !_isPlayingLadderTransition)
            {
                LetGoOfLadder();
            }
            else if (IsGrounded && !IsCrouching && !IsClimbingLadder && CanJump)
            {
                _jumpQueued = true;

                if (stamina != null)
                    stamina.ConsumeJumpStamina();
            }
        }

        if (_interactAction.WasPerformedThisFrame())
        {
            if (IsClimbingLadder)
                LetGoOfLadder();
            else if (TryFindLadder(out Ladder ladder))
            {
                if (transform.position.y >= ladder.TipPoint.y)
                    EnterLadderFromTop(ladder);
                else
                    EnterLadder(ladder);
            }
            else if (TryFindDoor(out Door door))
            {
                door.Toggle();
            }
        }

        UpdateCrouch();
        UpdatePeek();
    }

    private void UpdatePeek()
    {
        float rawPeek = _peekAction.ReadValue<float>();

        if (!IsGrounded || IsSprinting || IsClimbingLadder)
        {
            PeekAmount = 0f;
            return;
        }

        bool isMoving = _moveInput.sqrMagnitude > 0.01f;

        if (IsCrouching)
            PeekAmount = isMoving ? 0f : rawPeek;
        else
            PeekAmount = isMoving ? rawPeek * 0.5f : rawPeek;
    }

    private void FixedUpdate()
    {
        CheckGrounded();

        if (IsClimbingLadder)
        {
            UpdateLadderClimb();
            return;
        }

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

    private bool TryFindLadder(out Ladder ladder)
    {
        ladder = null;
        Collider[] hits = Physics.OverlapSphere(transform.position, ladderInteractDistance);

        foreach (Collider hit in hits)
        {
            if (!hit.CompareTag(ladderTag))
                continue;

            ladder = hit.GetComponentInParent<Ladder>();
            if (ladder != null)
                return true;
        }

        return false;
    }

    private bool TryFindDoor(out Door door)
    {
        door = null;
        Vector3 origin = transform.position + Vector3.up * (_capsule.height * 0.5f);

        if (Physics.Raycast(origin, transform.forward, out RaycastHit hit, doorInteractDistance) && hit.collider.CompareTag(doorTag))
            door = hit.collider.GetComponentInParent<Door>();

        return door != null;
    }

    private void EnterLadder(Ladder ladder)
    {
        _activeLadder = ladder;
        IsClimbingLadder = true;
        _isPlayingLadderTransition = false;
        _isEnteringLadder = true;
        _isEnteringFromTop = false;
        _ladderEnterStart = transform.position;
        _ladderEnterStartRotation = transform.rotation;
        _ladderEnterT = 0f;
        _rb.useGravity = false;
        _rb.linearVelocity = Vector3.zero;
        _capsule.enabled = false;

        if (playerAnimator != null)
            playerAnimator.PlayLadderEnter();
    }

    private void EnterLadderFromTop(Ladder ladder)
    {
        _activeLadder = ladder;
        IsClimbingLadder = true;
        _isPlayingLadderTransition = false;
        _isEnteringLadder = true;
        _isEnteringFromTop = true;
        _ladderEnterStart = transform.position;
        _ladderEnterStartRotation = transform.rotation;
        _ladderEnterT = 0f;
        _rb.useGravity = false;
        _rb.linearVelocity = Vector3.zero;
        _capsule.enabled = false;
    }

    private void ExitLadder()
    {
        IsClimbingLadder = false;
        _isEnteringLadder = false;
        _isEnteringFromTop = false;
        _isPlayingLadderTransition = false;
        _ladderTransitionReversed = false;
        _activeLadder = null;
        _rb.useGravity = true;
        _capsule.enabled = true;
    }

    private void LetGoOfLadder()
    {
        Vector3 jumpOffDirection = -_activeLadder.Forward;
        ExitLadder();
        _rb.linearVelocity = jumpOffDirection * ladderJumpOffForce;
    }

    private void UpdateLadderClimb()
    {
        if (_activeLadder == null)
        {
            ExitLadder();
            return;
        }

        if (_isEnteringLadder)
        {
            UpdateLadderEnter();
            return;
        }

        if (_isPlayingLadderTransition)
        {
            UpdateLadderTransition();
            return;
        }

        if (_moveInput.y > 0.1f && transform.position.y >= _activeLadder.TipPoint.y)
        {
            StartLadderFinish();
            return;
        }

        if (_moveInput.y < -0.1f && transform.position.y <= _activeLadder.BotStart.y)
        {
            ExitLadder();
            return;
        }

        Vector3 horizontalOffset = _activeLadder.BotStart - transform.position;
        horizontalOffset.y = 0f;

        Vector3 verticalVelocity = Vector3.up * (_moveInput.y * ladderClimbSpeed);
        _rb.linearVelocity = verticalVelocity + horizontalOffset * ladderSnapSpeed;
    }

    private void UpdateLadderEnter()
    {
        _ladderEnterT += Time.fixedDeltaTime / ladderEnterDuration;

        Vector3 targetPosition;
        if (_isEnteringFromTop)
        {
            targetPosition = _activeLadder.TopStart;
        }
        else
        {
            float grabHeight = Mathf.Clamp(_ladderEnterStart.y, _activeLadder.BotStart.y, _activeLadder.TipPoint.y);
            targetPosition = new Vector3(_activeLadder.BotStart.x, grabHeight, _activeLadder.BotStart.z);
        }

        Quaternion targetRotation = Quaternion.LookRotation(_activeLadder.Forward, Vector3.up);

        if (_ladderEnterT >= 1f)
        {
            _rb.MovePosition(targetPosition);
            transform.rotation = targetRotation;
            _isEnteringLadder = false;

            if (_isEnteringFromTop)
            {
                _isEnteringFromTop = false;
                StartLadderReverseEnter();
            }

            return;
        }

        _rb.MovePosition(Vector3.Lerp(_ladderEnterStart, targetPosition, _ladderEnterT));
        transform.rotation = Quaternion.Slerp(_ladderEnterStartRotation, targetRotation, _ladderEnterT);
    }

    private void StartLadderFinish()
    {
        _isPlayingLadderTransition = true;
        _ladderTransitionReversed = false;
        _rb.linearVelocity = Vector3.zero;

        if (playerAnimator != null)
            playerAnimator.PlayLadderFinish();
    }

    private void StartLadderReverseEnter()
    {
        _isPlayingLadderTransition = true;
        _ladderTransitionReversed = true;
        _rb.linearVelocity = Vector3.zero;

        if (playerAnimator != null)
            playerAnimator.PlayLadderEnterFromTop();
    }

    private void UpdateLadderTransition()
    {
        float t = playerAnimator != null ? playerAnimator.LadderTransitionProgress : (_ladderTransitionReversed ? 0f : 1f);
        bool complete = _ladderTransitionReversed ? t <= 0f : t >= 1f;

        if (!complete)
            return;

        if (_ladderTransitionReversed)
        {
            _isPlayingLadderTransition = false;
            _ladderTransitionReversed = false;

            if (playerAnimator != null)
                playerAnimator.PlayLadderEnterFromTopComplete();
        }
        else
        {
            ExitLadder();
        }
    }

    public void ApplyLadderFinishMotion(Vector3 deltaPosition)
    {
        if (_isPlayingLadderTransition)
            _rb.MovePosition(transform.position + deltaPosition);
    }

    private void UpdateCrouch()
    {
        if (IsClimbingLadder)
            return;

        bool wantsCrouch = _crouchAction.IsPressed();
        bool blockedFromStanding = IsCrouching && !wantsCrouch && !HasHeadroomToStand();

        IsCrouching = IsGroundedStable && (wantsCrouch || blockedFromStanding);

        _capsule.height = IsCrouching ? crouchHeight : standingHeight;
        _capsule.center = new Vector3(0f, _capsule.height * 0.5f, 0f);
    }

    private bool HasHeadroomToStand()
    {
        float radius = _capsule.radius * 0.95f;
        Vector3 origin = transform.position + Vector3.up * radius;
        float castDistance = standingHeight - radius * 2f;

        return !Physics.SphereCast(origin, radius, Vector3.up, out _, castDistance);
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
        _stepClimbDurationActual = IsCrouching ? stepClimbDurationCrouch : (IsSprinting ? stepClimbDurationSprint : stepClimbDurationWalk);
    }

    private void UpdateStepClimb()
    {
        if (_moveInput.sqrMagnitude < 0.01f)
        {
            _isClimbingStep = false;
            return;
        }

        _stepClimbT += Time.fixedDeltaTime / _stepClimbDurationActual;

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

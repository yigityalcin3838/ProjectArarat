using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private InputActionAsset inputActions;

    [Header("Debug")]
    [SerializeField] private float slowMoTimeScale = 0.2f;

    [Header("Movement")]
    [SerializeField] private float walkSpeed = 5f;
    [SerializeField] private float sprintSpeed = 8f;
    [SerializeField] private float jumpForce = 6f;

    [Header("Movement Feel")]
    [SerializeField] private float acceleration = 40f;
    [SerializeField] private float airMultiplier = 0.4f;

    [Header("Ground Check")]
    [SerializeField] private float groundCheckDistance = 0.15f;
    [SerializeField] private float maxSlopeAngle = 45f;
    [SerializeField] private float airborneGraceTime = 0.15f;

    [Header("Crouch")]
    [SerializeField] private float crouchSpeed = 2.5f;
    [SerializeField] private float standingHeight = 1.8f;
    [SerializeField] private float crouchHeight = 1f;

    [Header("Animator Link")]
    [SerializeField] private PlayerAnimator playerAnimator;
    [SerializeField] private PlayerStamina stamina;

    [Header("Interaction")]
    [SerializeField] private float enterTransitionDuration = 0.4f;

    [Header("Ladder")]
    [SerializeField] private string ladderTag = "Ladder";
    [SerializeField] private float ladderInteractDistance = 1.5f;
    [SerializeField] private float ladderClimbSpeed = 3f;
    [SerializeField] private float ladderJumpOffForce = 4f;

    [Header("Door")]
    [SerializeField] private string doorTag = "Door";
    [SerializeField] private float doorInteractDistance = 2f;

    [Header("Car")]
    [SerializeField] private string carDoorTag = "CarDoorLeft";
    [SerializeField] private float carInteractDistance = 2f;

    public float PeekAmount { get; private set; }
    public bool IsInCar { get; private set; }
    public float CarSpeedRatio => _activeCar != null ? _activeCar.SpeedRatio : 0f;

    public bool IsGrounded { get; private set; }
    public bool IsGroundedStable => IsGrounded || (Time.time - _lastGroundedTime) < airborneGraceTime;
    public bool IsCrouching { get; private set; }
    public Vector3 Velocity => _velocity;
    public Vector2 MoveInput => _moveInput;
    public float WalkSpeed => walkSpeed;
    public float SprintSpeed => sprintSpeed;
    public bool IsSprinting => IsGrounded && !IsCrouching && !IsInCar && _sprintAction.IsPressed() && _moveInput.sqrMagnitude > 0.01f && HasStamina;
    public bool IsSprintingStable => IsGroundedStable && !IsCrouching && !IsInCar && _sprintAction.IsPressed() && _moveInput.sqrMagnitude > 0.01f && HasStamina;
    public bool IsClimbingLadder { get; private set; }
    public bool IsMovementLocked => _isEnteringLadder || _isPlayingLadderTransition || IsInCar;

    private bool HasStamina => stamina == null || stamina.CurrentStamina > 0f;
    private bool CanJump => stamina == null || stamina.HasEnoughForJump;

    private CharacterController _characterController;
    private Vector3 _velocity;
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

    private Car _activeCar;
    private bool _isEnteringCar;
    private Vector3 _carEnterStart;
    private Quaternion _carEnterStartRotation;
    private float _carEnterT;
    private bool _isPlayingCarTransition;
    private bool _carTransitionReversed;
    private bool _isWaitingForCarShutdown;

    private Vector2 _moveInput;
    private bool _jumpQueued;
    private float _lastGroundedTime;
    private Vector3 _groundNormal = Vector3.up;

    private void Awake()
    {
        _characterController = GetComponent<CharacterController>();
        _characterController.slopeLimit = maxSlopeAngle;

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
        if (Keyboard.current != null && Keyboard.current.tKey.wasPressedThisFrame)
            Time.timeScale = Mathf.Approximately(Time.timeScale, 1f) ? slowMoTimeScale : 1f;

        _moveInput = _moveAction.ReadValue<Vector2>();

        if (_jumpAction.WasPerformedThisFrame())
        {
            if (IsClimbingLadder && !_isEnteringLadder && !_isPlayingLadderTransition)
            {
                LetGoOfLadder();
            }
            else if (IsGrounded && !IsCrouching && !IsClimbingLadder && !IsInCar && CanJump)
            {
                _jumpQueued = true;

                if (stamina != null)
                    stamina.ConsumeJumpStamina();
            }
        }

        if (_interactAction.WasPerformedThisFrame())
        {
            if (IsClimbingLadder)
            {
                if (!_isEnteringLadder && !_isPlayingLadderTransition)
                    LetGoOfLadder();
            }
            else if (IsInCar)
            {
                if (!_isEnteringCar && !_isPlayingCarTransition && _activeCar != null && _activeCar.IsReadyToDrive)
                    RequestExitCar();
            }
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
            else if (TryFindCarDoor(out Car car))
            {
                EnterCar(car);
            }
        }

        UpdateCrouch();
        UpdatePeek();

        CheckGrounded();

        if (IsClimbingLadder)
        {
            UpdateLadderClimb();
            return;
        }

        if (IsInCar)
        {
            UpdateCarState();
            return;
        }

        ApplyMovement();
        ApplyGravity();
        _characterController.Move(_velocity * Time.deltaTime);
    }

    private void UpdatePeek()
    {
        float rawPeek = _peekAction.ReadValue<float>();

        if (!IsGrounded || IsSprinting || IsClimbingLadder || IsInCar)
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
        Vector3 origin = transform.position + Vector3.up * (_characterController.height * 0.5f);

        if (Physics.Raycast(origin, transform.forward, out RaycastHit hit, doorInteractDistance) && hit.collider.CompareTag(doorTag))
            door = hit.collider.GetComponentInParent<Door>();

        return door != null;
    }

    private bool TryFindCarDoor(out Car car)
    {
        car = null;
        Vector3 origin = transform.position + Vector3.up * (_characterController.height * 0.5f);

        if (Physics.Raycast(origin, transform.forward, out RaycastHit hit, carInteractDistance) && hit.collider.CompareTag(carDoorTag))
            car = hit.collider.GetComponentInParent<Car>();

        return car != null;
    }

    private void EnterCar(Car car)
    {
        _activeCar = car;
        IsInCar = true;
        _isEnteringCar = true;
        _isPlayingCarTransition = false;
        _carEnterStart = transform.position;
        _carEnterStartRotation = transform.rotation;
        _carEnterT = 0f;
        _velocity = Vector3.zero;
        _characterController.enabled = false;
    }

    private void RequestExitCar()
    {
        if (_isWaitingForCarShutdown)
            return;

        _isWaitingForCarShutdown = true;

        if (_activeCar != null)
            _activeCar.RequestShutdown();
    }

    private void ExitCar()
    {
        _isPlayingCarTransition = true;
        _carTransitionReversed = true;
        _velocity = Vector3.zero;

        if (_activeCar != null)
        {
            _activeCar.IsBeingDriven = false;
            _activeCar.PlayDoor(false);
        }

        if (playerAnimator != null)
        {
            playerAnimator.PlayCarExit();
            playerAnimator.ClearHandIKTargets();
        }
    }

    private void ExitCarComplete()
    {
        if (playerAnimator != null)
            playerAnimator.ClearHandIKTargets();

        IsInCar = false;
        _isEnteringCar = false;
        _isPlayingCarTransition = false;
        _carTransitionReversed = false;
        _isWaitingForCarShutdown = false;
        _activeCar = null;
        _characterController.enabled = true;
    }

    private void UpdateCarState()
    {
        if (_activeCar == null)
        {
            ExitCarComplete();
            return;
        }

        if (_isEnteringCar)
        {
            UpdateCarEnter();
            return;
        }

        if (_isPlayingCarTransition)
        {
            UpdateCarTransition();
            return;
        }

        if (_isWaitingForCarShutdown && _activeCar.IsReadyToExit)
        {
            _isWaitingForCarShutdown = false;
            ExitCar();
            return;
        }

        transform.position = _activeCar.FrontLeft;
        transform.rotation = Quaternion.LookRotation(_activeCar.Forward, _activeCar.Up);

        if (playerAnimator != null)
        {
            bool keepAtHandbrake = (_activeCar.IsHandbrakeHeld && !_activeCar.IsGearAnimating && !_activeCar.IsGearBlipping) || _activeCar.IsHandbrakeAnimating;
            Transform rightHandTarget;
            if (_activeCar.IsDoorAnimating)
                rightHandTarget = _activeCar.RightHandGrip;
            else if (_activeCar.IsEngineAnimating)
                rightHandTarget = _activeCar.KeyGrip;
            else if (keepAtHandbrake)
                rightHandTarget = _activeCar.HandBrakeGrip;
            else if (_activeCar.IsGearAnimating || _activeCar.IsGearBlipping)
                rightHandTarget = _activeCar.GearGrip;
            else
                rightHandTarget = _activeCar.RightHandGrip;
            playerAnimator.SetRightHandIKTarget(rightHandTarget, _activeCar.HandIKTransitionDuration);

            Transform leftHandTarget;
            if (_activeCar.IsDoorAnimating)
                leftHandTarget = _activeCar.DoorGrip;
            else if (_activeCar.IsHornPressed)
                leftHandTarget = _activeCar.HornGrip;
            else
                leftHandTarget = _activeCar.LeftHandGrip;
            playerAnimator.SetLeftHandIKTarget(leftHandTarget, _activeCar.HandIKTransitionDuration);
        }
    }

    private void UpdateCarEnter()
    {
        _carEnterT += Time.deltaTime / enterTransitionDuration;

        Vector3 targetPosition = _activeCar.DoorLeft;
        Quaternion targetRotation = Quaternion.LookRotation(_activeCar.Forward, Vector3.up);

        if (_carEnterT >= 1f)
        {
            transform.position = targetPosition;
            transform.rotation = targetRotation;
            _isEnteringCar = false;
            StartCarEntryAnimation();
            return;
        }

        transform.position = Vector3.Lerp(_carEnterStart, targetPosition, _carEnterT);
        transform.rotation = Quaternion.Slerp(_carEnterStartRotation, targetRotation, _carEnterT);
    }

    private void StartCarEntryAnimation()
    {
        _isPlayingCarTransition = true;
        _carTransitionReversed = false;
        _velocity = Vector3.zero;

        if (playerAnimator != null)
            playerAnimator.PlayCarEnter();

        if (_activeCar != null)
            _activeCar.PlayDoor(true);
    }

    private void UpdateCarTransition()
    {
        float t = playerAnimator != null ? playerAnimator.CarTransitionProgress : (_carTransitionReversed ? 0f : 1f);
        bool complete = _carTransitionReversed ? t <= 0f : t >= 1f;
        float clampedT = Mathf.Clamp01(t);

        transform.position = Vector3.Lerp(_activeCar.DoorLeft, _activeCar.FrontLeft, clampedT);

        Quaternion levelRotation = Quaternion.LookRotation(_activeCar.Forward, Vector3.up);
        Quaternion tiltedRotation = Quaternion.LookRotation(_activeCar.Forward, _activeCar.Up);
        transform.rotation = Quaternion.Slerp(levelRotation, tiltedRotation, clampedT);

        if (playerAnimator != null)
        {
            Transform leftHandTarget = _activeCar.IsDoorAnimating ? _activeCar.DoorGrip : _activeCar.LeftHandGrip;
            playerAnimator.SetLeftHandIKTarget(leftHandTarget, _activeCar.HandIKTransitionDuration);
        }

        if (!complete)
            return;

        if (_carTransitionReversed)
        {
            ExitCarComplete();
        }
        else
        {
            _isPlayingCarTransition = false;

            if (_activeCar != null)
                _activeCar.IsBeingDriven = true;

            if (playerAnimator != null)
            {
                playerAnimator.PlayCarEnterComplete();

                if (_activeCar != null)
                {
                    Transform leftHandTarget = _activeCar.IsDoorAnimating ? _activeCar.DoorGrip : _activeCar.LeftHandGrip;
                    playerAnimator.SetLeftHandIKTarget(leftHandTarget, _activeCar.HandIKTransitionDuration);
                    playerAnimator.SetRightHandIKTarget(_activeCar.RightHandGrip, _activeCar.HandIKTransitionDuration);
                }
            }
        }
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
        _velocity = Vector3.zero;
        _characterController.enabled = false;

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
        _velocity = Vector3.zero;
        _characterController.enabled = false;
    }

    private void ExitLadder()
    {
        IsClimbingLadder = false;
        _isEnteringLadder = false;
        _isEnteringFromTop = false;
        _isPlayingLadderTransition = false;
        _ladderTransitionReversed = false;
        _activeLadder = null;
        _characterController.enabled = true;
    }

    private void LetGoOfLadder()
    {
        Vector3 jumpOffDirection = -_activeLadder.Forward;
        ExitLadder();
        _velocity = jumpOffDirection * ladderJumpOffForce;
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

        Vector3 climbVelocity = Vector3.up * (_moveInput.y * ladderClimbSpeed);
        transform.position += climbVelocity * Time.deltaTime;
    }

    private void UpdateLadderEnter()
    {
        _ladderEnterT += Time.deltaTime / enterTransitionDuration;

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
            transform.position = targetPosition;
            transform.rotation = targetRotation;
            _isEnteringLadder = false;

            if (_isEnteringFromTop)
            {
                _isEnteringFromTop = false;
                StartLadderReverseEnter();
            }

            return;
        }

        transform.position = Vector3.Lerp(_ladderEnterStart, targetPosition, _ladderEnterT);
        transform.rotation = Quaternion.Slerp(_ladderEnterStartRotation, targetRotation, _ladderEnterT);
    }

    private void StartLadderFinish()
    {
        _isPlayingLadderTransition = true;
        _ladderTransitionReversed = false;
        _velocity = Vector3.zero;

        if (playerAnimator != null)
            playerAnimator.PlayLadderFinish();
    }

    private void StartLadderReverseEnter()
    {
        _isPlayingLadderTransition = true;
        _ladderTransitionReversed = true;
        _velocity = Vector3.zero;

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

    public void ApplyTransitionMotion(Vector3 deltaPosition)
    {
        if (_isPlayingLadderTransition)
            transform.position += deltaPosition;
    }

    private void UpdateCrouch()
    {
        if (IsClimbingLadder || IsInCar)
            return;

        bool wantsCrouch = _crouchAction.IsPressed();
        bool blockedFromStanding = IsCrouching && !wantsCrouch && !HasHeadroomToStand();

        IsCrouching = IsGroundedStable && (wantsCrouch || blockedFromStanding);

        _characterController.height = IsCrouching ? crouchHeight : standingHeight;
        _characterController.center = new Vector3(0f, _characterController.height * 0.5f, 0f);
    }

    private bool HasHeadroomToStand()
    {
        float radius = _characterController.radius * 0.95f;
        Vector3 origin = transform.position + Vector3.up * radius;
        float castDistance = standingHeight - radius * 2f;

        return !Physics.SphereCast(origin, radius, Vector3.up, out _, castDistance);
    }

    private void CheckGrounded()
    {
        float radius = _characterController.radius * 0.9f;
        Vector3 origin = transform.position + Vector3.up * (radius + 0.05f);

        bool hitGround = Physics.SphereCast(origin, radius, Vector3.down, out RaycastHit hit, groundCheckDistance + 0.05f);
        IsGrounded = hitGround && Vector3.Angle(hit.normal, Vector3.up) <= maxSlopeAngle;
        _groundNormal = hitGround ? hit.normal : Vector3.up;

        if (IsGrounded)
            _lastGroundedTime = Time.time;
    }

    private void ApplyMovement()
    {
        Vector3 wishDir = transform.right * _moveInput.x + transform.forward * _moveInput.y;
        wishDir = Vector3.ClampMagnitude(wishDir, 1f);

        float currentSpeed = IsCrouching ? crouchSpeed : (IsSprinting ? sprintSpeed : walkSpeed);
        Vector3 targetHorizontal = wishDir * currentSpeed;

        float accel = acceleration * (IsGrounded ? 1f : airMultiplier);
        Vector3 currentHorizontal = new Vector3(_velocity.x, 0f, _velocity.z);
        currentHorizontal = Vector3.MoveTowards(currentHorizontal, targetHorizontal, accel * Time.deltaTime);

        _velocity.x = currentHorizontal.x;
        _velocity.z = currentHorizontal.z;
    }

    private void ApplyGravity()
    {
        if (_jumpQueued)
        {
            _velocity.y = jumpForce;
            _jumpQueued = false;
            return;
        }

        if (IsGrounded && _velocity.y < 0f)
            _velocity.y = -2f;
        else
            _velocity.y += Physics.gravity.y * Time.deltaTime;
    }
}

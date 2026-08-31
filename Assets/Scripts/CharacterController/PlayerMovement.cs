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
    [SerializeField] private PlayerItems items;

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

    public bool IsInCar { get; private set; }
    public float CarSpeedRatio => _activeCar != null ? _activeCar.SpeedRatio : 0f;

    public bool IsGrounded { get; private set; }
    public bool IsGroundedStable => IsGrounded || (Time.time - _lastGroundedTime) < airborneGraceTime;
    public bool IsCrouching { get; private set; }
    public Vector3 Velocity => _velocity;
    public Vector2 MoveInput => _moveInput;
    public float WalkSpeed => walkSpeed;
    public float SprintSpeed => sprintSpeed;
    public bool IsSprinting => IsGrounded && !IsCrouching && !IsInCar && !_sprintBlocked && _sprintAction.IsPressed() && _moveInput.sqrMagnitude > 0.01f && HasStamina;
    public bool IsSprintingStable => IsGroundedStable && !IsCrouching && !IsInCar && !_sprintBlocked && _sprintAction.IsPressed() && _moveInput.sqrMagnitude > 0.01f && HasStamina;
    public bool IsClimbingLadder => _ladderPhase != LadderPhase.None;

    // True whenever something other than the player is driving the character:
    // every ladder phase except the climb itself, and the whole time in a car.
    public bool IsMovementLocked =>
        (_ladderPhase != LadderPhase.None && _ladderPhase != LadderPhase.Climbing) || IsInCar;

    // How far through the slide to a ladder or car entry point the root is: 0 as
    // it starts, 1 once it has arrived, and 1 whenever no slide is running.
    // Anything that has to finish in step with that slide paces itself by this
    // rather than by a duration of its own -- the root is turning to face the
    // ladder or car as it goes, so a second clock running alongside would have
    // the body finish squaring up before or after it got there, and the model
    // would read as rotating twice.
    public float EntrySlideProgress =>
        _isEnteringCar ? Mathf.Clamp01(_carEnterT)
        : _ladderPhase == LadderPhase.Approaching ? Mathf.Clamp01(_ladderApproachT)
        : 1f;

    public bool IsSlidingToEntry => _isEnteringCar || _ladderPhase == LadderPhase.Approaching;

    // One-frame pulses for equipped items (e.g. Pistol) to react to with a
    // one-shot effect (a jump/land kick) -- read-only, mirrors IsGrounded etc.
    public bool JumpedThisFrame { get; private set; }
    public bool LandedThisFrame { get; private set; }

    // Lets an equipped item (e.g. Pistol) block sprinting while active (e.g. while
    // aiming down sights), without PlayerMovement needing to know anything about
    // items -- same push-values-in pattern as PlayerLook's FOV override.
    public void SetSprintBlocked(bool blocked) => _sprintBlocked = blocked;

    // Same pattern -- caps ground speed to crouchSpeed while aiming, without this
    // script needing to know anything about items.
    public void SetAimSpeedOverride(bool isAiming) => _aimSpeedOverride = isAiming;

    private bool HasStamina => stamina == null || stamina.CurrentStamina > 0f;
    private bool CanJump => stamina == null || stamina.HasEnoughForJump;

    private CharacterController _characterController;
    private Vector3 _velocity;
    private bool _aimSpeedOverride;
    private InputAction _moveAction;
    private InputAction _jumpAction;
    private InputAction _sprintAction;
    private InputAction _crouchAction;
    private InputAction _interactAction;

    private Ladder _activeLadder;
    // One phase at a time, in place of a handful of booleans that between them
    // could describe states the ladder has no meaning for. Everything about being
    // on a ladder is derived from this and _activeLadder: no combination of flags
    // to keep consistent, and nothing to forget to clear on the way out.
    private enum LadderPhase
    {
        None,
        Approaching,  // walking to the grab point; a timed slide owns the root
        Mounting,     // an authored clip owns it: taking hold of the ladder
        Climbing,     // the player owns it, up and down the rail
        Dismounting,  // an authored clip owns it again: getting off at the top
    }

    private LadderPhase _ladderPhase;

    // Which end each authored phase happens at. They are separate because they
    // can differ: climbing on at the top and off at the bottom is an ordinary
    // way to use a ladder.
    private bool _isMountingFromTop;
    private bool _isDismountingAtTop;
    private Vector3 _ladderApproachStart;
    private Quaternion _ladderApproachStartRotation;
    private float _ladderApproachT;

    // Animation-driven phases wait on the animator reaching a state. A trigger
    // can be swallowed, so waiting is never open-ended -- same guard as the turn
    // recovery in PlayerAnimator, for the same reason: the alternative is a
    // player stuck on a ladder with no way off.
    private float _ladderAnimWaitTimer;
    private const float LadderAnimStartGrace = 0.5f;

    // What was asked for but hasn't started yet, while the hands are cleared.
    private Ladder _pendingLadder;
    private bool _pendingLadderFromTop;
    private Car _pendingCar;

    private Car _activeCar;
    private bool _isEnteringCar;
    private Vector3 _carEnterStart;
    private Quaternion _carEnterStartRotation;
    private float _carEnterT;
    private bool _isPlayingCarTransition;
    private bool _carTransitionReversed;
    private bool _isWaitingForCarShutdown;

    private Vector2 _moveInput;
    private bool _sprintBlocked;
    private bool _jumpQueued;
    private bool _wasGrounded;
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
    }

    private void OnEnable()
    {
        _moveAction.Enable();
        _jumpAction.Enable();
        _sprintAction.Enable();
        _crouchAction.Enable();
        _interactAction.Enable();
    }

    private void OnDisable()
    {
        _moveAction.Disable();
        _jumpAction.Disable();
        _sprintAction.Disable();
        _crouchAction.Disable();
        _interactAction.Disable();
    }

    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current.tKey.wasPressedThisFrame)
            Time.timeScale = Mathf.Approximately(Time.timeScale, 1f) ? slowMoTimeScale : 1f;

        JumpedThisFrame = false;
        LandedThisFrame = false;

        _moveInput = _moveAction.ReadValue<Vector2>();

        if (_jumpAction.WasPerformedThisFrame())
        {
            if (_ladderPhase == LadderPhase.Climbing)
            {
                LetGoOfLadder();
            }
            else if (IsGrounded && !IsCrouching && !IsClimbingLadder && !IsInCar && CanJump)
            {
                _jumpQueued = true;
                JumpedThisFrame = true;

                if (stamina != null)
                    stamina.ConsumeJumpStamina();
            }
        }

        if (_interactAction.WasPerformedThisFrame())
        {
            if (IsClimbingLadder)
            {
                if (_ladderPhase == LadderPhase.Climbing)
                    LetGoOfLadder();
            }
            else if (IsInCar)
            {
                if (!_isEnteringCar && !_isPlayingCarTransition && _activeCar != null && _activeCar.IsReadyToDrive)
                    RequestExitCar();
            }
            else if (TryFindLadder(out Ladder ladder))
            {
                RequestEntry(ladder, transform.position.y >= ladder.TipPoint.y, null);
            }
            else if (TryFindDoor(out Door door))
            {
                door.Toggle();
            }
            else if (TryFindCarDoor(out Car car))
            {
                RequestEntry(null, false, car);
            }
        }

        UpdateCrouch();
        UpdatePendingEntry();

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
            playerAnimator.SetRightHandIKTarget(rightHandTarget, transitionDuration: _activeCar.HandIKTransitionDuration);

            Transform leftHandTarget;
            if (_activeCar.IsDoorAnimating)
                leftHandTarget = _activeCar.DoorGrip;
            else if (_activeCar.IsHornPressed)
                leftHandTarget = _activeCar.HornGrip;
            else
                leftHandTarget = _activeCar.LeftHandGrip;
            playerAnimator.SetLeftHandIKTarget(leftHandTarget, transitionDuration: _activeCar.HandIKTransitionDuration);
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
            playerAnimator.SetLeftHandIKTarget(leftHandTarget, transitionDuration: _activeCar.HandIKTransitionDuration);
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
                    playerAnimator.SetLeftHandIKTarget(leftHandTarget, transitionDuration: _activeCar.HandIKTransitionDuration);
                    playerAnimator.SetRightHandIKTarget(_activeCar.RightHandGrip, transitionDuration: _activeCar.HandIKTransitionDuration);
                }
            }
        }
    }

    // Asking to get on a ladder or into a car and actually starting are separate
    // steps, because a ladder and a steering wheel both want two free hands.
    // Anything still in hand is put away first and the entry waits for the
    // animation to finish -- otherwise the character walks over to the ladder
    // stowing a pistol, and reaches for the rung mid-holster.
    private void RequestEntry(Ladder ladder, bool ladderFromTop, Car car)
    {
        _pendingLadder = ladder;
        _pendingLadderFromTop = ladderFromTop;
        _pendingCar = car;

        items?.StowEquippedItem();
    }

    private void UpdatePendingEntry()
    {
        if (_pendingLadder == null && _pendingCar == null)
            return;

        if (items != null && items.AreHandsBusy)
            return;

        Ladder ladder = _pendingLadder;
        Car car = _pendingCar;
        _pendingLadder = null;
        _pendingCar = null;

        // Re-checked rather than taken on trust: putting an item away takes long
        // enough to walk out of reach, and being yanked back to a ladder left
        // behind several strides ago is worse than the press doing nothing.
        if (ladder != null)
        {
            if (TryFindLadder(out Ladder stillInReach) && stillInReach == ladder)
                EnterLadder(ladder, _pendingLadderFromTop);
        }
        else if (TryFindCarDoor(out Car stillAtDoor) && stillAtDoor == car)
        {
            EnterCar(car);
        }
    }

    // Approaching from above or below differs only in where the grab point ends
    // up and which clip mounts the character -- not in the flow, which is why
    // there is one entry point rather than two near-identical ones.
    private void EnterLadder(Ladder ladder, bool fromTop)
    {
        _activeLadder = ladder;
        _ladderPhase = LadderPhase.Approaching;
        _isMountingFromTop = fromTop;
        _ladderApproachStart = transform.position;
        _ladderApproachStartRotation = transform.rotation;
        _ladderApproachT = 0f;
        _velocity = Vector3.zero;
        _characterController.enabled = false;
    }

    private void ExitLadder()
    {
        _ladderPhase = LadderPhase.None;
        _isMountingFromTop = false;
        _isDismountingAtTop = false;
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

        switch (_ladderPhase)
        {
            case LadderPhase.Approaching: UpdateLadderApproach(); break;
            case LadderPhase.Mounting: UpdateLadderMount(); break;
            case LadderPhase.Climbing: UpdateLadderRail(); break;
            case LadderPhase.Dismounting: UpdateLadderDismount(); break;
        }
    }

    // The point on the rail the character takes hold of. Coming from below it is
    // whatever height they were already at, clamped to the rail; from above it is
    // the top. One function, so the two approaches can't drift apart.
    private Vector3 GetLadderGrabPoint()
    {
        if (_isMountingFromTop)
            return _activeLadder.TopStart;

        float grabHeight = Mathf.Clamp(_ladderApproachStart.y, _activeLadder.BotStart.y, _activeLadder.TipPoint.y);
        return new Vector3(_activeLadder.BotStart.x, grabHeight, _activeLadder.BotStart.z);
    }

    private void UpdateLadderApproach()
    {
        _ladderApproachT += Time.deltaTime / enterTransitionDuration;

        Vector3 targetPosition = GetLadderGrabPoint();
        Quaternion targetRotation = Quaternion.LookRotation(_activeLadder.Forward, Vector3.up);

        if (_ladderApproachT < 1f)
        {
            transform.position = Vector3.Lerp(_ladderApproachStart, targetPosition, _ladderApproachT);
            transform.rotation = Quaternion.Slerp(_ladderApproachStartRotation, targetRotation, _ladderApproachT);
            return;
        }

        transform.position = targetPosition;
        transform.rotation = targetRotation;
        BeginLadderPhase(LadderPhase.Mounting);

        // Triggered on arrival, not on the command. Both of these take the
        // animator into their state straight from AnyState, so firing them up
        // front had the character mounting while still walking over to the ladder.
        if (playerAnimator == null)
            return;

        if (_isMountingFromTop)
            playerAnimator.PlayLadderMountFromTop();
        else
            playerAnimator.PlayLadderMountFromBottom();
    }

    private void UpdateLadderMount()
    {
        // The top mount is the dismount clip run backwards, so its progress counts
        // down rather than up. That is the only place the reversal is dealt with.
        float progress = 1f;
        if (playerAnimator != null)
        {
            progress = _isMountingFromTop
                ? playerAnimator.LadderDismountProgress
                : playerAnimator.LadderMountProgress;
        }

        if (!IsLadderAnimationFinished(progress, countsDown: _isMountingFromTop))
            return;

        if (playerAnimator != null)
            playerAnimator.PlayLadderMountComplete();

        BeginLadderPhase(LadderPhase.Climbing);
    }

    private void UpdateLadderRail()
    {
        if (_moveInput.y > 0.1f && transform.position.y >= _activeLadder.TipPoint.y)
        {
            StartLadderDismount(atTop: true);
            return;
        }

        if (_moveInput.y < -0.1f && transform.position.y <= _activeLadder.BotStart.y)
        {
            StartLadderDismount(atTop: false);
            return;
        }

        transform.position += Vector3.up * (_moveInput.y * ladderClimbSpeed) * Time.deltaTime;
    }

    private void StartLadderDismount(bool atTop)
    {
        _isDismountingAtTop = atTop;
        BeginLadderPhase(LadderPhase.Dismounting);
        _velocity = Vector3.zero;

        if (playerAnimator == null)
            return;

        if (atTop)
            playerAnimator.PlayLadderDismountAtTop();
        else
            playerAnimator.PlayLadderDismountAtBottom();
    }

    private void UpdateLadderDismount()
    {
        // Off the top is the exit clip forwards; off the bottom is the mount clip
        // backwards -- letting go of the rail is taking hold of it in reverse.
        float progress = 1f;
        if (playerAnimator != null)
        {
            progress = _isDismountingAtTop
                ? playerAnimator.LadderDismountProgress
                : playerAnimator.LadderMountProgress;
        }

        if (IsLadderAnimationFinished(progress, countsDown: !_isDismountingAtTop))
            ExitLadder();
    }

    private void BeginLadderPhase(LadderPhase phase)
    {
        _ladderPhase = phase;
        _ladderAnimWaitTimer = 0f;
    }

    // Shared by both animation-driven phases. Progress comes back as -1 until the
    // animator has actually reached the state, which is exactly what a swallowed
    // trigger looks like -- so that is waited out rather than read as "at the
    // start", and once the wait runs long the phase reports done anyway. Being
    // stranded on a ladder with no way off is far worse than a missing animation.
    private bool IsLadderAnimationFinished(float progress, bool countsDown)
    {
        if (progress < 0f)
        {
            _ladderAnimWaitTimer += Time.deltaTime;
            return _ladderAnimWaitTimer >= LadderAnimStartGrace;
        }

        _ladderAnimWaitTimer = 0f;
        return countsDown ? progress <= 0f : progress >= 1f;
    }

    public void ApplyTransitionMotion(Vector3 deltaPosition)
    {
        // Only the authored phases move the character by root motion; the climb
        // itself is driven by input, and the approach by its own slide.
        if (_ladderPhase == LadderPhase.Mounting || _ladderPhase == LadderPhase.Dismounting)
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

        // Triggers ignored: an interaction zone overhead isn't something you
        // can bump your head on, and letting one block standing up would trap
        // the player crouched for no visible reason.
        return !Physics.SphereCast(origin, radius, Vector3.up, out _, castDistance, ~0, QueryTriggerInteraction.Ignore);
    }

    private void CheckGrounded()
    {
        float radius = _characterController.radius * 0.9f;
        Vector3 origin = transform.position + Vector3.up * (radius + 0.05f);

        // Triggers ignored: walking over a door zone or a fog volume would
        // otherwise register as standing on it, and its surface normal would
        // be fed to the slope check as if it were real ground.
        bool hitGround = Physics.SphereCast(origin, radius, Vector3.down, out RaycastHit hit, groundCheckDistance + 0.05f, ~0, QueryTriggerInteraction.Ignore);
        IsGrounded = hitGround && Vector3.Angle(hit.normal, Vector3.up) <= maxSlopeAngle;
        _groundNormal = hitGround ? hit.normal : Vector3.up;

        // _velocity.y still holds the pre-impact falling speed here -- ApplyGravity
        // (which resets it once grounded) hasn't run yet this frame.
        if (IsGrounded && !_wasGrounded && _velocity.y < 0f)
            LandedThisFrame = true;
        _wasGrounded = IsGrounded;

        if (IsGrounded)
            _lastGroundedTime = Time.time;
    }

    private void ApplyMovement()
    {
        Vector3 wishDir = transform.right * _moveInput.x + transform.forward * _moveInput.y;
        wishDir = Vector3.ClampMagnitude(wishDir, 1f);

        float currentSpeed = (IsCrouching || _aimSpeedOverride) ? crouchSpeed : (IsSprinting ? sprintSpeed : walkSpeed);
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

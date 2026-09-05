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

    // Metres per second squared: how fast the horizontal velocity closes on the
    // speed the input is asking for, in both directions. The animator reads the
    // resulting velocity rather than the raw key press, so this is also what
    // ramps the legs from idle into a stride -- which is why there is no damping
    // on the blend parameter any more. The ramp is the character's, not a filter
    // laid over the top of an instant one.
    [SerializeField] private float acceleration = 40f;

    // Applied to the whole movement vector the moment there is any backward
    // component, so a back-diagonal is no faster than going straight back --
    // otherwise retreating at an angle becomes the quickest way to retreat.
    [Header("Backward Speeds")]
    [SerializeField] private float walkBackSpeed = 3.5f;
    [SerializeField] private float sprintBackSpeed = 5f;

    [Header("Ground Check")]
    [SerializeField] private float groundCheckDistance = 0.15f;
    [SerializeField] private float maxSlopeAngle = 45f;
    [SerializeField] private float airborneGraceTime = 0.15f;

    [Header("Crouch")]
    [SerializeField] private float crouchSpeed = 2.5f;
    [SerializeField] private float crouchBackSpeed = 1.8f;
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

    // What the character is actually doing across the ground, rather than what is
    // being asked of it. The two differ for as long as acceleration takes, and
    // that difference is the whole point: the legs follow this, so they ramp with
    // the body instead of snapping to a key press the body hasn't caught up with
    // yet. World space -- whoever reads it decides which axis it cares about.
    public Vector3 HorizontalVelocity => new Vector3(_velocity.x, 0f, _velocity.z);

    // Ground speed as a fraction of a walk: 0 standing, 1 at a full walk, 1.6 at a
    // sprint, half that crouched -- and every value in between while accelerating.
    //
    // Exists so a bob doesn't have to be told which gait it is in. Crouching is
    // slower and a sprint faster because the character is moving slower and faster,
    // not because a multiplier was looked up, so there is nothing to keep in step
    // with the speeds themselves and nothing that stays wrong through the stretch
    // where the character is between two gaits.
    public float GaitSpeedRatio => walkSpeed > 0f
        ? HorizontalVelocity.magnitude / walkSpeed
        : 0f;

    public float WalkSpeed => walkSpeed;
    public float SprintSpeed => sprintSpeed;
    // Everything a sprint needs except the ground check, which is the one thing the
    // two versions below disagree about. Factored because they were the same long
    // expression written twice, and a condition added to one and missed on the other
    // would have the view and the hands believe different things about the same
    // frame.
    private bool CanSprint =>
        !IsCrouching
        && !IsInCar
        && !_sprintBlocked
        && _sprintAction.IsPressed()
        && HasStamina
        && _moveInput.sqrMagnitude > 0.01f
        // No running backwards. Nobody sprints in reverse, and the animation has no
        // idea how to: the locomotion set has one run, played forwards, so a backward
        // sprint is a character sliding backwards at speed with its legs driving the
        // wrong way.
        //
        // Zero passes, so a pure sidestep still sprints. Only a backward component
        // stops it -- which is the rule as stated, and tightening it to demand actual
        // forward intent is one character change away if strafing at speed reads
        // wrong too.
        && _moveInput.y >= 0f;

    public bool IsSprinting => IsGrounded && CanSprint;
    public bool IsSprintingStable => IsGroundedStable && CanSprint;
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

    // One-frame pulses for equipped items (e.g. Weapon) to react to with a
    // one-shot effect (a jump/land kick) -- read-only, mirrors IsGrounded etc.
    public bool JumpedThisFrame { get; private set; }
    public bool LandedThisFrame { get; private set; }

    // Lets an equipped item (e.g. Weapon) block sprinting while active (e.g. while
    // aiming down sights), without PlayerMovement needing to know anything about
    // items -- same push-values-in pattern as PlayerLook's FOV override.
    public void SetSprintBlocked(bool blocked) => _sprintBlocked = blocked;

    // Same pattern -- caps ground speed to crouchSpeed while aiming, without this
    // script needing to know anything about items.
    public void SetAimSpeedOverride(bool isAiming) => _aimSpeedOverride = isAiming;

    // Whether an item currently has the player aiming. Pushed in by the item rather
    // than worked out here, and exposed because it is a stance like crouching is:
    // anything that behaves differently down the sights can read it without having
    // to know which item put it there.
    public bool IsAiming => _aimSpeedOverride;

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

        // Ignored outright while an item is being drawn or put away, rather than
        // queued behind it. Entry already waits for the hands, so queueing would
        // work -- but a ladder taken half a second after the key was pressed, with
        // a holster playing in between, reads as the input having been dropped.
        // Refusing it while the hands are busy is at least legible.
        bool handsChangingItem = items != null && items.IsChangingItem;

        if (_interactAction.WasPerformedThisFrame() && !handsChangingItem)
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

        // Masked, because this takes the nearest hit and then checks its tag -- so
        // anything in the way is not ignored, it fails the tag check and the door
        // stops being usable. A severed arm lying against it would be enough.
        if (Physics.Raycast(origin, transform.forward, out RaycastHit hit, doorInteractDistance, GameLayers.Queryable)
            && hit.collider.CompareTag(doorTag))
            door = hit.collider.GetComponentInParent<Door>();

        return door != null;
    }

    private bool TryFindCarDoor(out Car car)
    {
        car = null;
        Vector3 origin = transform.position + Vector3.up * (_characterController.height * 0.5f);

        // Masked for the same reason as the door: nearest hit, then a tag check.
        if (Physics.Raycast(origin, transform.forward, out RaycastHit hit, carInteractDistance, GameLayers.Queryable)
            && hit.collider.CompareTag(carDoorTag))
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

        // Frozen for the length of a turn-in-place, in whichever state the turn
        // started. The standing and crouching turns are separate states with no
        // transition between them, so the legs finish the turn they began however
        // hard the key is held -- and letting the crouch go through anyway left
        // the capsule, the camera and the speed all describing a crouch the body
        // was visibly not in. Nothing about crouching happens until the turn is
        // done, at which point the key is read again and it takes effect at once.
        if (playerAnimator != null && playerAnimator.IsTurningInPlace)
            return;

        bool wantsCrouch = _crouchAction.IsPressed();
        bool blockedFromStanding = IsCrouching && !wantsCrouch && !HasHeadroomToStand();

        IsCrouching = IsGroundedStable && (wantsCrouch || blockedFromStanding);

        _characterController.height = IsCrouching ? crouchHeight : standingHeight;

        // Raised by the skin width, not just half the height. A CharacterController
        // never lets its capsule touch a surface -- it always keeps that much
        // clearance -- so with the capsule bottom sitting exactly on the transform
        // origin the whole character comes to rest a skin width above the floor,
        // and the model's feet hang in the air by the same amount. Lifting the
        // capsule instead puts the origin, and with it the feet, back on the ground.
        _characterController.center = new Vector3(
            0f,
            _characterController.height * 0.5f + _characterController.skinWidth,
            0f);
    }

    private bool HasHeadroomToStand()
    {
        float radius = _characterController.radius * 0.95f;
        Vector3 origin = transform.position + Vector3.up * radius;
        float castDistance = standingHeight - radius * 2f;

        // Triggers ignored: an interaction zone overhead isn't something you
        // can bump your head on, and letting one block standing up would trap
        // the player crouched for no visible reason.
        return !Physics.SphereCast(origin, radius, Vector3.up, out _, castDistance, GameLayers.Queryable, QueryTriggerInteraction.Ignore);
    }

    private void CheckGrounded()
    {
        float radius = _characterController.radius * 0.9f;
        Vector3 origin = transform.position + Vector3.up * (radius + 0.05f);

        // Triggers ignored: walking over a door zone or a fog volume would
        // otherwise register as standing on it, and its surface normal would
        // be fed to the slope check as if it were real ground.
        //
        // Debris is excluded by the mask for a sharper version of the same problem.
        // The player passes through it, but the cast would still find it and report
        // ground appearing and vanishing underfoot -- which is landing, over and
        // over, shake and all.
        bool hitGround = Physics.SphereCast(origin, radius, Vector3.down, out RaycastHit hit, groundCheckDistance + 0.05f, GameLayers.Queryable, QueryTriggerInteraction.Ignore);
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

    // The speed a given gait settles at. Public because the animator divides the
    // actual velocity by it to place the character on its blend tree: the same
    // figures that decide how fast the character moves are the ones that decide
    // what a full stride means, so a full press reads as exactly one walk (or
    // one run) rather than whatever fraction a hardcoded reference left over.
    //
    // Crouching has no run half at all, so it answers with its own walk speed
    // either way. Aiming borrows the crouch pair rather than having a pair of
    // its own: the point of both is the same restricted, deliberate pace.
    public float GetGaitSpeed(bool running, bool backward)
    {
        if (IsCrouching || _aimSpeedOverride)
            return backward ? crouchBackSpeed : crouchSpeed;

        if (running)
            return backward ? sprintBackSpeed : sprintSpeed;

        return backward ? walkBackSpeed : walkSpeed;
    }

    private void ApplyMovement()
    {
        Vector3 wishDir = transform.right * _moveInput.x + transform.forward * _moveInput.y;
        wishDir = Vector3.ClampMagnitude(wishDir, 1f);

        Vector3 targetHorizontal = wishDir * GetGaitSpeed(IsSprinting, _moveInput.y < 0f);
        Vector3 currentHorizontal = new Vector3(_velocity.x, 0f, _velocity.z);

        // Symmetric on purpose: the same figure that gets the character moving is
        // the one that brings it to a stop, so releasing a key coasts out over the
        // same span the press ramped in over.
        currentHorizontal = Vector3.MoveTowards(
            currentHorizontal, targetHorizontal, acceleration * Time.deltaTime);

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

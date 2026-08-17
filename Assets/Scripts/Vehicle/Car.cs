using FMOD.Studio;
using FMODUnity;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class Car : MonoBehaviour
{
    [Header("Anchor Points")]
    [SerializeField] private Transform doorLeft;
    [SerializeField] private Transform frontLeft;
    [SerializeField] private Transform leftHandGrip;
    [SerializeField] private Transform rightHandGrip;
    [SerializeField] private Transform handBrakeGrip;
    [SerializeField] private Transform hornGrip;
    [SerializeField] private Transform gearGrip;
    [SerializeField] private Transform doorGrip;
    [SerializeField] private Transform keyGrip;

    [Header("Body")]
    [SerializeField] private Collider[] bodyColliders;

    [Header("Wheel Colliders")]
    [SerializeField] private WheelCollider frontLeftWheelCollider;
    [SerializeField] private WheelCollider frontRightWheelCollider;
    [SerializeField] private WheelCollider rearLeftWheelCollider;
    [SerializeField] private WheelCollider rearRightWheelCollider;

    [Header("Wheel Meshes")]
    [SerializeField] private Transform frontLeftWheelMesh;
    [SerializeField] private Transform frontRightWheelMesh;
    [SerializeField] private Transform rearLeftWheelMesh;
    [SerializeField] private Transform rearRightWheelMesh;

    [Header("Input")]
    [SerializeField] private InputActionAsset inputActions;

    [Header("Driving")]
    [SerializeField] private float motorTorque = 1500f;
    [SerializeField] private float reverseTorqueMultiplier = 0.5f;
    [SerializeField] private float maxSpeedKmh = 120f;

    [Header("Braking")]
    [SerializeField] private float brakeTorque = 3000f;
    [SerializeField] private float parkBrakeTorque = 3000f;

    [Header("Steering")]
    [SerializeField] private float maxSteerAngle = 30f;
    [SerializeField] private float steerSpeed = 120f;
    [SerializeField] private Transform steeringWheel;
    [SerializeField] private float steeringWheelRotationMultiplier = 3f;
    [SerializeField] private Vector3 steeringWheelRotationAxis = Vector3.forward;

    [Header("Handbrake")]
    [SerializeField] private float handbrakeTorque = 8000f;

    [Header("Animation")]
    [SerializeField] private Animator carAnimator;
    [SerializeField] private AnimationClip engineAnimationClip;
    [SerializeField] private AnimationClip doorAnimationClip;
    [SerializeField] private AnimationClip handbrakeUpClip;
    [SerializeField] private AnimationClip handbrakeDownClip;
    [SerializeField] private AnimationClip gearFrontClip;
    [SerializeField] private AnimationClip gearBackClip;
    [SerializeField] private AnimationClip gearFrontReverseClip;
    [SerializeField] private AnimationClip gearBackReverseClip;

    [Header("Vehicle State")]
    [SerializeField] private float handArrivalDelay = 0.15f;
    [SerializeField] private float handIKTransitionDuration = 0.08f;

    [Header("Anti-Roll")]
    [SerializeField] private float antiRollStiffness = 5000f;

    [Header("Drift")]
    [SerializeField] private float driftSidewaysStiffness = 0.5f;

    [Header("UI")]
    [SerializeField] private TMP_Text speedText;

    [Header("Audio")]
    [SerializeField] private StudioEventEmitter engineStartEmitter;
    [SerializeField] private StudioEventEmitter engineIdleEmitter;
    [SerializeField] private StudioEventEmitter engineStopEmitter;
    [SerializeField] private StudioEventEmitter handbrakeOnEmitter;
    [SerializeField] private StudioEventEmitter handbrakeOffEmitter;
    [SerializeField] private StudioEventEmitter hornEmitter;
    [SerializeField] private StudioEventEmitter doorOpenInteriorEmitter;
    [SerializeField] private StudioEventEmitter doorOpenExteriorEmitter;
    [SerializeField] private StudioEventEmitter doorCloseInteriorEmitter;
    [SerializeField] private StudioEventEmitter doorCloseExteriorEmitter;
    [SerializeField] private float engineRevChangeSpeed = 1f;
    [SerializeField] private float engineRevShiftDropSpeed = 6f;

    private static readonly int HandbrakePullHash = Animator.StringToHash("HandbrakePull");
    private static readonly int HandbrakeReleaseHash = Animator.StringToHash("HandbrakeRelease");
    private static readonly int GearShiftFrontHash = Animator.StringToHash("GearShiftFront");
    private static readonly int GearShiftBackHash = Animator.StringToHash("GearShiftBack");
    private static readonly int VehicleResetHash = Animator.StringToHash("VehicleReset");
    private static readonly int GearReturnToIdleHash = Animator.StringToHash("GearReturnToIdle");
    private static readonly int PlayDoorHash = Animator.StringToHash("PlayDoor");
    private static readonly int PlayEngineAnimationHash = Animator.StringToHash("PlayEngineAnimation");
    private const string EngineIdleRevParameter = "Rev";
    private const int ForwardGearCount = 5;
    private const int ReverseGearCount = 1;
    private const float GearInputThreshold = 0.1f;
    private const float GearShiftStopThreshold = 0.5f;

    private enum GearPosition { Idle, Front, Back }
    private enum HandbrakePosition { Idle, Up, Down }
    private enum ShutdownPhase { None, Handbrake, Engine, Gear, Complete }

    public Vector3 DoorLeft => doorLeft.position;
    public Vector3 FrontLeft => frontLeft.position;
    public Vector3 Forward => doorLeft.forward;
    public Vector3 Up => doorLeft.up;
    public Transform LeftHandGrip => leftHandGrip;
    public Transform RightHandGrip => rightHandGrip;
    public Transform HandBrakeGrip => handBrakeGrip;
    public Transform HornGrip => hornGrip;
    public Transform GearGrip => gearGrip;
    public Transform DoorGrip => doorGrip;
    public Transform KeyGrip => keyGrip;
    public float HandIKTransitionDuration => handIKTransitionDuration;

    // Time-based off the clip's own length, same reasoning as IsEngineAnimating below.
    public bool IsDoorAnimating => _doorAnimationTimer < (doorAnimationClip != null ? doorAnimationClip.length : 0f);

    // False for the whole window between sitting down and the handbrake actually finishing its
    // auto-release (engine starting, handbrake still Idle/animating) -- exiting shouldn't be
    // requestable until the car is genuinely ready to drive.
    public bool IsReadyToDrive => _handbrakeTarget == HandbrakePosition.Down && !IsHandbrakeAnimating;

    public bool IsHandbrakeHeld { get; private set; }
    public bool IsHornPressed { get; private set; }
    public float SpeedRatio => Mathf.Clamp01(_rb.linearVelocity.magnitude / (maxSpeedKmh / 3.6f));

    // Time-based on purpose, not an Animator query: querying GetCurrentAnimatorStateInfo in the
    // same frame SetTrigger fires still reflects the OLD state (Unity hasn't processed the
    // trigger yet), which caused these to report "done" a frame too early. Compared against the
    // actual clip's own length (not a manually-kept-in-sync duration field), so it always
    // matches however long that clip really is. Target Idle means the auto-release sequence
    // hasn't started yet (just entered, still waiting on engine start) -- nothing is actually
    // happening to the handbrake yet, so this must read as NOT busy: the hand stays at the wheel
    // and only moves to the grip once the release target/animation genuinely begins, avoiding a
    // premature grab (and, without a short-circuit here, the stale post-reset timer would also
    // false-positive regardless of target).
    public bool IsHandbrakeAnimating
    {
        get
        {
            if (_handbrakeTarget == HandbrakePosition.Idle)
                return false;

            if (_handbrakeAnimatedState != _handbrakeTarget)
                return true;

            AnimationClip clip = _handbrakeAnimatedState == HandbrakePosition.Up ? handbrakeUpClip : handbrakeDownClip;
            return _handbrakeAnimationTimer < (clip != null ? clip.length : 0f);
        }
    }

    public bool IsGearAnimating
    {
        get
        {
            if (_shutdownPhase == ShutdownPhase.Gear)
                return true;

            if (_gearAnimatedState != _gearTarget && _gearTarget != GearPosition.Idle)
                return true;

            if (_gearTarget == GearPosition.Idle)
                return false;

            return _gearAnimationTimer < GearClipLength(_gearTarget);
        }
    }

    // A cosmetic "blip" during acceleration (see UpdateEngineRev/StartGearBlip) that replays the
    // reverse-then-forward gear clip in sync with each simulated upshift, purely for the hand's
    // benefit -- deliberately kept separate from IsGearAnimating so it never blocks ApplyDrive's
    // torque.
    public bool IsGearBlipping => _gearBlipActive;

    public bool IsReadyToExit => _shutdownPhase == ShutdownPhase.Complete;

    public void RequestShutdown()
    {
        _shutdownRequested = true;
    }

    // DoorEntrance.anim now plays the whole open-hold-close cycle in one forward pass, so a
    // single trigger covers both entering and exiting -- isEntry only decides which two of the
    // four door emitters the two Animation Events (see PlayDoorSound) end up picking.
    public void PlayDoor(bool isEntry)
    {
        _doorIsEntry = isEntry;
        _doorAnimationTimer = 0f;

        if (carAnimator != null)
            carAnimator.SetTrigger(PlayDoorHash);
    }

    // Called from the two Animation Events on DoorEntrance.anim: one where the door reaches
    // fully open, one where it reaches fully closed again. Entering: open while still outside,
    // close once seated inside. Exiting: open while still inside, close once stepped outside.
    public void PlayDoorSound(string which)
    {
        if (which == "Open")
        {
            StudioEventEmitter emitter = _doorIsEntry ? doorOpenExteriorEmitter : doorOpenInteriorEmitter;
            emitter?.Play();
        }
        else if (which == "Close")
        {
            StudioEventEmitter emitter = _doorIsEntry ? doorCloseInteriorEmitter : doorCloseExteriorEmitter;
            emitter?.Play();
        }
    }

    // Time-based off the clip's own length (not a manually-kept-in-sync duration field, not an
    // end-of-clip Animation Event) -- reads AnimationClip.length directly, so it always matches
    // however long Start_stop.anim actually is, even if that changes later.
    public bool IsEngineAnimating => _engineAnimationTimer < (engineAnimationClip != null ? engineAnimationClip.length : 0f);

    public void PlayEngineAnimation(bool isStarting)
    {
        _engineAnimationIsStarting = isStarting;
        _engineAnimationTimer = 0f;

        if (carAnimator != null)
            carAnimator.SetTrigger(PlayEngineAnimationHash);
    }

    // Called from the Animation Event on Start_stop.anim (mid-clip, wherever the key-turn sound
    // should land) -- this is what actually plays the engine start/stop FMOD sound and (for
    // start) begins the same _waitingForEngineStart poll PlayEngineIdle already relied on, so
    // the rest of the auto-release sequencing is untouched.
    public void PlayEngineAnimationSound()
    {
        if (_engineAnimationIsStarting)
        {
            if (engineStartEmitter != null)
            {
                engineStartEmitter.Play();
                _waitingForEngineStart = true;
            }
            else
            {
                PlayEngineIdle();
            }
        }
        else if (engineStopEmitter != null)
        {
            engineStopEmitter.Play();
        }
    }

    public bool IsBeingDriven
    {
        get => _isBeingDriven;
        set
        {
            if (_isBeingDriven == value)
                return;

            _isBeingDriven = value;

            if (_isBeingDriven)
            {
                _pendingEngineStartAnimation = true;
                ResetVehicleState();
            }
            else
            {
                SettleToIdleDisplay();
            }
        }
    }

    private Rigidbody _rb;
    private InputAction _moveAction;
    private InputAction _handbrakeAction;
    private InputAction _hornAction;
    private float _currentSteerAngle;
    private float _rearLeftNormalSidewaysStiffness;
    private float _rearRightNormalSidewaysStiffness;
    private Quaternion _steeringWheelBaseRotation;
    private bool _isBeingDriven;
    private bool _waitingForEngineStart;
    private bool _pendingAutoRelease;
    private bool _pendingEngineStartAnimation;
    private bool _engineAnimationIsStarting;
    private float _engineAnimationTimer;
    private bool _doorIsEntry;
    private float _doorAnimationTimer;
    private float _engineRevAmount;
    private int _lastRevGearIndex;
    private bool _gearBlipActive;
    private bool _gearBlipReturning;
    private float _gearBlipTimer;

    private GearPosition _gearTarget;
    private GearPosition _gearAnimatedState;
    private GearPosition _gearReverseFrom;
    private float _gearAnimationDelayTimer;
    private float _gearAnimationTimer;

    private HandbrakePosition _handbrakeTarget;
    private bool _lastHandbrakeHeld;
    private HandbrakePosition _handbrakeAnimatedState;
    private float _handbrakeAnimationDelayTimer;
    private float _handbrakeAnimationTimer;

    private bool _shutdownRequested;
    private ShutdownPhase _shutdownPhase;
    private float _gearReturnDelayTimer;
    private bool _gearReturnFired;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        _rearLeftNormalSidewaysStiffness = rearLeftWheelCollider.sidewaysFriction.stiffness;
        _rearRightNormalSidewaysStiffness = rearRightWheelCollider.sidewaysFriction.stiffness;

        if (steeringWheel != null)
            _steeringWheelBaseRotation = steeringWheel.localRotation;

        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        _moveAction = playerMap.FindAction("Move");
        _handbrakeAction = playerMap.FindAction("Jump");
        _hornAction = playerMap.FindAction("Attack");

        foreach (Collider bodyCollider in bodyColliders)
        {
            if (bodyCollider == null)
                continue;

            Physics.IgnoreCollision(bodyCollider, frontLeftWheelCollider);
            Physics.IgnoreCollision(bodyCollider, frontRightWheelCollider);
            Physics.IgnoreCollision(bodyCollider, rearLeftWheelCollider);
            Physics.IgnoreCollision(bodyCollider, rearRightWheelCollider);
        }
    }

    private void OnEnable()
    {
        _moveAction.Enable();
        _handbrakeAction.Enable();
        _hornAction.Enable();
    }

    private void OnDisable()
    {
        _moveAction.Disable();
        _handbrakeAction.Disable();
        _hornAction.Disable();
    }

    private void Update()
    {
        UpdateSpeedText();
        UpdateEngineAudio();
        UpdateEngineIdleParameter();
        UpdateHorn();
    }

    private void UpdateHorn()
    {
        if (!IsBeingDriven)
        {
            IsHornPressed = false;
            return;
        }

        if (_hornAction.WasPressedThisFrame())
        {
            IsHornPressed = true;
            if (hornEmitter != null)
                hornEmitter.Play();
        }
        else if (_hornAction.WasReleasedThisFrame())
        {
            IsHornPressed = false;
            if (hornEmitter != null)
                hornEmitter.Stop();
        }
    }

    private void UpdateEngineAudio()
    {
        if (_waitingForEngineStart)
        {
            engineStartEmitter.EventInstance.getPlaybackState(out PLAYBACK_STATE startState);
            if (startState == PLAYBACK_STATE.STOPPED)
            {
                _waitingForEngineStart = false;
                PlayEngineIdle();
            }
        }

    }

    private void PlayEngineIdle()
    {
        if (engineIdleEmitter != null)
            engineIdleEmitter.Play();
    }

    // Only rises while the throttle is actually held -- letting off drops it back toward idle
    // even if the car is still coasting at speed, matching a real engine falling off-throttle.
    // While held, it tracks SpeedRatio (0 at a stop, 1 exactly at maxSpeedKmh) reshaped into a
    // sawtooth over ForwardGearCount bands -- pitch climbs within a "gear" and drops back down
    // crossing into the next one, like an upshift, while the last band still reaches exactly 1
    // right at maxSpeedKmh. Reverse only has one band (no shifting feel). Smoothed rather than
    // following the target 1:1 so small physics-frame jitter doesn't flutter the pitch -- drops
    // use their own (faster) rate so the upshift actually reads as a hard drop, not a slope.
    private void UpdateEngineRev(float throttleInput)
    {
        float target = 0f;

        if (IsBeingDriven && Mathf.Abs(throttleInput) > GearInputThreshold)
        {
            int gearCount = _gearTarget == GearPosition.Back ? ReverseGearCount : ForwardGearCount;
            int gearIndex = Mathf.Min(Mathf.FloorToInt(SpeedRatio * gearCount), gearCount - 1);
            target = SpeedRatio * gearCount - gearIndex;

            if (gearIndex > _lastRevGearIndex && _gearTarget != GearPosition.Idle &&
                _shutdownPhase == ShutdownPhase.None && !IsGearAnimating && !_gearBlipActive)
                StartGearBlip();

            _lastRevGearIndex = gearIndex;
        }
        else if (!IsBeingDriven)
        {
            _lastRevGearIndex = 0;
        }

        float rate = target < _engineRevAmount ? engineRevShiftDropSpeed : engineRevChangeSpeed;
        _engineRevAmount = Mathf.MoveTowards(_engineRevAmount, target, rate * Time.fixedDeltaTime);
    }

    // Purely cosmetic: replays the current gear's reverse-then-forward clip so the hand appears
    // to bump the shifter in sync with each simulated upshift. Fully independent of _gearTarget/
    // _gearAnimatedState/_gearAnimationTimer so it never touches IsGearAnimating or ApplyDrive's
    // torque gating -- the car keeps driving through it uninterrupted.
    private void StartGearBlip()
    {
        _gearBlipActive = true;
        _gearBlipReturning = false;
        _gearBlipTimer = 0f;

        if (carAnimator != null)
            carAnimator.SetTrigger(GearReturnToIdleHash);
    }

    // Two beats long: the reverse-out clip first, then the return-to-forward clip -- IsGearBlipping
    // (and so the hand's grip on the shifter) stays true through both, only releasing once the
    // return has had the actual forward clip's own length to play out, not the instant it fires.
    private void UpdateGearBlip()
    {
        if (!_gearBlipActive)
            return;

        _gearBlipTimer += Time.fixedDeltaTime;

        if (!_gearBlipReturning)
        {
            if (_gearBlipTimer < GearReverseClipLength(_gearTarget))
                return;

            _gearBlipReturning = true;
            _gearBlipTimer = 0f;

            if (carAnimator != null)
                carAnimator.SetTrigger(_gearTarget == GearPosition.Front ? GearShiftFrontHash : GearShiftBackHash);

            return;
        }

        if (_gearBlipTimer < GearClipLength(_gearTarget))
            return;

        _gearBlipActive = false;
        _gearBlipReturning = false;
    }

    // Feeds the ramped rev amount into the Idle event's own "Rev" parameter every frame so
    // FMOD's parameter automation (pitch/filter set up on the Idle event itself) can react to
    // it -- no pitch/filter math here, that stays entirely on the FMOD side.
    private void UpdateEngineIdleParameter()
    {
        if (IsBeingDriven && engineIdleEmitter != null)
            engineIdleEmitter.SetParameter(EngineIdleRevParameter, _engineRevAmount);
    }

    // Only silences the running engine immediately -- the actual stop sound now plays from the
    // Animation Event on Start_stop.anim (see PlayEngineAnimationSound), fired once the
    // shutdown sequence reaches ShutdownPhase.Engine.
    private void StopEngineAudio()
    {
        _waitingForEngineStart = false;

        if (engineStartEmitter != null)
            engineStartEmitter.Stop();

        if (engineIdleEmitter != null)
            engineIdleEmitter.Stop();

        if (hornEmitter != null)
            hornEmitter.Stop();

        IsHornPressed = false;
    }

    private void PlayHandbrakeFeedback(bool engaged)
    {
        StudioEventEmitter emitter = engaged ? handbrakeOnEmitter : handbrakeOffEmitter;
        if (emitter != null)
            emitter.Play();

        if (carAnimator != null)
            carAnimator.SetTrigger(engaged ? HandbrakePullHash : HandbrakeReleaseHash);
    }

    private void SettleToIdleDisplay()
    {
        _handbrakeTarget = HandbrakePosition.Idle;
        _handbrakeAnimatedState = HandbrakePosition.Idle;
        _gearTarget = GearPosition.Idle;
        _gearAnimatedState = GearPosition.Idle;

        if (carAnimator != null)
            carAnimator.SetTrigger(VehicleResetHash);
    }

    private void ResetVehicleState()
    {
        _gearTarget = GearPosition.Idle;
        _gearAnimatedState = GearPosition.Idle;
        _gearAnimationDelayTimer = 0f;
        _gearAnimationTimer = 0f;

        _handbrakeTarget = HandbrakePosition.Idle;
        _lastHandbrakeHeld = _handbrakeAction != null && _handbrakeAction.IsPressed();
        _handbrakeAnimatedState = HandbrakePosition.Idle;
        _handbrakeAnimationDelayTimer = 0f;
        _handbrakeAnimationTimer = 0f;

        _shutdownRequested = false;
        _shutdownPhase = ShutdownPhase.None;
        _gearReturnDelayTimer = 0f;
        _gearReturnFired = false;
        _pendingAutoRelease = true;

        if (carAnimator != null)
            carAnimator.SetTrigger(VehicleResetHash);
    }

    private void FixedUpdate()
    {
        Vector2 input = IsBeingDriven ? _moveAction.ReadValue<Vector2>() : Vector2.zero;
        IsHandbrakeHeld = IsBeingDriven && _handbrakeAction.IsPressed();
        UpdateEngineRev(input.y);
        UpdateGearBlip();

        // Runs regardless of IsBeingDriven -- the door starts (and can finish) playing before
        // the car ever becomes "being driven" (entry) and after it stops being driven (exit).
        _doorAnimationTimer += Time.fixedDeltaTime;

        // Physical brake force stays applied for the whole shutdown sequence (parked, not just
        // while the Handbrake phase is animating), but that must NOT leak into IsHandbrakeHeld --
        // PlayerMovement reads IsHandbrakeHeld to decide whether the hand should stay on the
        // handbrake grip, and it needs to be free to move to the gear grip once the Gear phase
        // starts even though the brake itself is still (correctly) engaged.
        bool handbrakeEngaged = IsBeingDriven && (IsHandbrakeHeld || _handbrakeTarget == HandbrakePosition.Up);

        if (IsBeingDriven)
        {
            _engineAnimationTimer += Time.fixedDeltaTime;

            // The engine-start animation only fires once the door has fully finished (see
            // below), so auto-release must wait on the whole chain -- door, then the animation
            // itself -- but not the start sound it fires mid-clip; the car is mechanically ready
            // to go the moment the key-turn animation completes, the sound just plays out on
            // its own from there.
            if (_pendingEngineStartAnimation && !IsDoorAnimating)
            {
                _pendingEngineStartAnimation = false;
                PlayEngineAnimation(true);
            }

            if (_pendingAutoRelease && !_pendingEngineStartAnimation && !IsEngineAnimating)
            {
                _pendingAutoRelease = false;
                _handbrakeTarget = HandbrakePosition.Down;
            }

            if (_shutdownRequested && _shutdownPhase == ShutdownPhase.None && !IsGearAnimating && !IsHandbrakeAnimating)
            {
                _shutdownRequested = false;
                _shutdownPhase = ShutdownPhase.Handbrake;
                _handbrakeTarget = HandbrakePosition.Up;
            }

            UpdateHandbrakeState(IsHandbrakeHeld);

            if (_shutdownPhase == ShutdownPhase.None)
                UpdateGearState(input.y);
            else
                UpdateShutdown();
        }

        ApplySteering(input.x);
        ApplyDrive(input.y, handbrakeEngaged);
        ApplyDriftGrip(handbrakeEngaged);
        ClampSpeed();
        ApplyAntiRoll(frontLeftWheelCollider, frontRightWheelCollider);
        ApplyAntiRoll(rearLeftWheelCollider, rearRightWheelCollider);
        SyncWheelMeshes();
    }

    private void UpdateShutdown()
    {
        switch (_shutdownPhase)
        {
            case ShutdownPhase.Handbrake:
                if (!IsHandbrakeAnimating)
                {
                    _shutdownPhase = ShutdownPhase.Engine;
                    StopEngineAudio();
                    PlayEngineAnimation(false);
                }
                break;

            case ShutdownPhase.Engine:
                if (!IsEngineAnimating)
                {
                    _shutdownPhase = ShutdownPhase.Gear;
                    SetGearTarget(GearPosition.Idle);
                }
                break;

            case ShutdownPhase.Gear:
                UpdateGearAnimation();
                if (IsGearSettled())
                    _shutdownPhase = ShutdownPhase.Complete;
                break;
        }
    }

    private void UpdateHandbrakeState(bool held)
    {
        if (held != _lastHandbrakeHeld && !IsGearAnimating && !IsGearBlipping)
        {
            _lastHandbrakeHeld = held;
            HandbrakePosition target = held ? HandbrakePosition.Up : HandbrakePosition.Down;

            if (target != _handbrakeTarget)
            {
                _handbrakeTarget = target;
                _handbrakeAnimationDelayTimer = 0f;
            }
        }

        if (_handbrakeAnimatedState != _handbrakeTarget && _handbrakeTarget != HandbrakePosition.Idle)
        {
            _handbrakeAnimationDelayTimer += Time.fixedDeltaTime;
            if (_handbrakeAnimationDelayTimer >= handArrivalDelay)
            {
                _handbrakeAnimatedState = _handbrakeTarget;
                _handbrakeAnimationTimer = 0f;
                PlayHandbrakeFeedback(_handbrakeTarget == HandbrakePosition.Up);
            }
        }

        _handbrakeAnimationTimer += Time.fixedDeltaTime;
    }

    private void UpdateGearState(float throttleInput)
    {
        GearPosition desired = _gearTarget;
        if (throttleInput > GearInputThreshold)
            desired = GearPosition.Front;
        else if (throttleInput < -GearInputThreshold)
            desired = GearPosition.Back;

        bool canShift = _gearTarget == GearPosition.Idle ||
            Mathf.Abs(Vector3.Dot(_rb.linearVelocity, transform.forward)) <= GearShiftStopThreshold;

        if (desired != _gearTarget && canShift && _handbrakeTarget == HandbrakePosition.Down && !IsHandbrakeAnimating && !_gearBlipActive)
            SetGearTarget(desired);

        UpdateGearAnimation();
    }

    private void SetGearTarget(GearPosition target)
    {
        _gearTarget = target;
        _gearAnimationDelayTimer = 0f;
        _gearReturnDelayTimer = 0f;
        _gearReturnFired = false;
    }

    private float GearClipLength(GearPosition position)
    {
        AnimationClip clip = position == GearPosition.Front ? gearFrontClip : gearBackClip;
        return clip != null ? clip.length : 0f;
    }

    private float GearReverseClipLength(GearPosition position)
    {
        AnimationClip clip = position == GearPosition.Front ? gearFrontReverseClip : gearBackReverseClip;
        return clip != null ? clip.length : 0f;
    }

    // Leaving a non-Idle gear always plays the reverse-out clip first. From there, GearIdle is
    // only the resting state right after entering the car or at the end of an exit shutdown --
    // a normal Front<->Back reversal commits straight to the new gear the moment the reverse
    // clip finishes (the hand never left the grip, so no extra arrival wait either), skipping
    // GearIdle entirely both in this state machine and in the controller's own transitions.
    private void UpdateGearAnimation()
    {
        if (_gearAnimatedState == _gearTarget && !_gearReturnFired)
        {
            _gearAnimationTimer += Time.fixedDeltaTime;
            return;
        }

        if (_gearAnimatedState != GearPosition.Idle)
        {
            _gearReturnDelayTimer += Time.fixedDeltaTime;
            if (_gearReturnDelayTimer < handArrivalDelay)
            {
                _gearAnimationTimer += Time.fixedDeltaTime;
                return;
            }

            if (carAnimator != null)
                carAnimator.SetTrigger(GearReturnToIdleHash);

            _gearReverseFrom = _gearAnimatedState;
            _gearAnimatedState = GearPosition.Idle;
            _gearReturnFired = true;
            _gearReturnDelayTimer = 0f;
            _gearAnimationTimer = 0f;
            return;
        }

        if (_gearReturnFired)
        {
            _gearReturnDelayTimer += Time.fixedDeltaTime;
            if (_gearReturnDelayTimer < GearReverseClipLength(_gearReverseFrom))
            {
                _gearAnimationTimer += Time.fixedDeltaTime;
                return;
            }

            _gearReturnFired = false;
            _gearReturnDelayTimer = 0f;

            if (_gearTarget != GearPosition.Idle)
            {
                _gearAnimatedState = _gearTarget;
                _gearAnimationTimer = 0f;
                if (carAnimator != null)
                    carAnimator.SetTrigger(_gearTarget == GearPosition.Front ? GearShiftFrontHash : GearShiftBackHash);
            }

            return;
        }

        if (_gearTarget == GearPosition.Idle)
        {
            _gearAnimationTimer += Time.fixedDeltaTime;
            return;
        }

        // Starting straight from Idle (first shift after entering the car) -- wait for the hand
        // to actually arrive at the grip before playing the shift animation.
        _gearAnimationDelayTimer += Time.fixedDeltaTime;
        if (_gearAnimationDelayTimer < handArrivalDelay)
        {
            _gearAnimationTimer += Time.fixedDeltaTime;
            return;
        }

        _gearAnimatedState = _gearTarget;
        _gearAnimationTimer = 0f;
        if (carAnimator != null)
            carAnimator.SetTrigger(_gearTarget == GearPosition.Front ? GearShiftFrontHash : GearShiftBackHash);
    }

    private bool IsGearSettled() => _gearAnimatedState == _gearTarget && !_gearReturnFired;

    private void ApplySteering(float steerInput)
    {
        float targetSteerAngle = steerInput * maxSteerAngle;
        _currentSteerAngle = Mathf.MoveTowards(_currentSteerAngle, targetSteerAngle, steerSpeed * Time.fixedDeltaTime);
        frontLeftWheelCollider.steerAngle = _currentSteerAngle;
        frontRightWheelCollider.steerAngle = _currentSteerAngle;

        if (steeringWheel != null)
            steeringWheel.localRotation = _steeringWheelBaseRotation * Quaternion.AngleAxis(-_currentSteerAngle * steeringWheelRotationMultiplier, steeringWheelRotationAxis);
    }

    private void ApplyDrive(float throttleInput, bool handbrake)
    {
        float forwardSpeed = Vector3.Dot(_rb.linearVelocity, transform.forward);
        bool isBraking = IsBeingDriven && ((throttleInput > 0.01f && forwardSpeed < -0.5f) || (throttleInput < -0.01f && forwardSpeed > 0.5f));

        bool canDrive = IsBeingDriven && _shutdownPhase == ShutdownPhase.None && _handbrakeTarget == HandbrakePosition.Down && !IsHandbrakeAnimating &&
            ((throttleInput > 0f && _gearTarget == GearPosition.Front && !IsGearAnimating) ||
             (throttleInput < 0f && _gearTarget == GearPosition.Back && !IsGearAnimating));

        float torque = 0f;
        if (!isBraking && canDrive)
        {
            torque = throttleInput * motorTorque;
            if (throttleInput < 0f)
                torque *= reverseTorqueMultiplier;
        }

        rearLeftWheelCollider.motorTorque = torque;
        rearRightWheelCollider.motorTorque = torque;

        float baseBrake = !IsBeingDriven ? parkBrakeTorque : (isBraking ? brakeTorque : 0f);
        frontLeftWheelCollider.brakeTorque = baseBrake;
        frontRightWheelCollider.brakeTorque = baseBrake;
        rearLeftWheelCollider.brakeTorque = handbrake ? handbrakeTorque : baseBrake;
        rearRightWheelCollider.brakeTorque = handbrake ? handbrakeTorque : baseBrake;
    }

    private void ApplyDriftGrip(bool handbrake)
    {
        SetSidewaysStiffness(rearLeftWheelCollider, handbrake ? driftSidewaysStiffness : _rearLeftNormalSidewaysStiffness);
        SetSidewaysStiffness(rearRightWheelCollider, handbrake ? driftSidewaysStiffness : _rearRightNormalSidewaysStiffness);
    }

    private static void SetSidewaysStiffness(WheelCollider wheelCollider, float stiffness)
    {
        WheelFrictionCurve friction = wheelCollider.sidewaysFriction;
        friction.stiffness = stiffness;
        wheelCollider.sidewaysFriction = friction;
    }

    private void ClampSpeed()
    {
        float maxSpeedMs = maxSpeedKmh / 3.6f;
        Vector3 velocity = _rb.linearVelocity;

        if (velocity.magnitude > maxSpeedMs)
            _rb.linearVelocity = velocity.normalized * maxSpeedMs;
    }

    private void ApplyAntiRoll(WheelCollider wheelL, WheelCollider wheelR)
    {
        float travelL = 1f;
        float travelR = 1f;

        bool groundedL = wheelL.GetGroundHit(out WheelHit hitL);
        if (groundedL)
            travelL = (-wheelL.transform.InverseTransformPoint(hitL.point).y - wheelL.radius) / wheelL.suspensionDistance;

        bool groundedR = wheelR.GetGroundHit(out WheelHit hitR);
        if (groundedR)
            travelR = (-wheelR.transform.InverseTransformPoint(hitR.point).y - wheelR.radius) / wheelR.suspensionDistance;

        float antiRollForce = (travelL - travelR) * antiRollStiffness;

        if (groundedL)
            _rb.AddForceAtPosition(wheelL.transform.up * -antiRollForce, wheelL.transform.position);
        if (groundedR)
            _rb.AddForceAtPosition(wheelR.transform.up * antiRollForce, wheelR.transform.position);
    }

    private void SyncWheelMeshes()
    {
        SyncWheelMesh(frontLeftWheelCollider, frontLeftWheelMesh);
        SyncWheelMesh(frontRightWheelCollider, frontRightWheelMesh);
        SyncWheelMesh(rearLeftWheelCollider, rearLeftWheelMesh);
        SyncWheelMesh(rearRightWheelCollider, rearRightWheelMesh);
    }

    private static void SyncWheelMesh(WheelCollider wheelCollider, Transform wheelMesh)
    {
        if (wheelMesh == null)
            return;

        wheelCollider.GetWorldPose(out Vector3 position, out Quaternion rotation);
        wheelMesh.SetPositionAndRotation(position, rotation);
    }

    private void UpdateSpeedText()
    {
        if (speedText == null)
            return;

        if (!IsBeingDriven)
        {
            speedText.text = string.Empty;
            return;
        }

        Vector3 horizontalVelocity = _rb.linearVelocity;
        horizontalVelocity.y = 0f;
        int speedKmh = Mathf.RoundToInt(horizontalVelocity.magnitude * 3.6f);
        speedText.text = $"{speedKmh} km/h";
    }
}

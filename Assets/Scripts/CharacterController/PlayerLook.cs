using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerLook : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private InputActionAsset inputActions;

    [Header("Look")]
    // The one thing that decides where the player is looking from. A plain child
    // of the capsule, driven entirely from here -- never parented to a bone and
    // never following one.
    //
    // It used to hard-lock to a socket on the head bone, which is what put every
    // frame of spine and neck animation straight into the view. Worse, the aim
    // rig's target hangs off this transform, so the rig twisted the spine, the
    // spine moved the head, the head moved the camera, and the camera moved the
    // target the rig was aiming at -- a closed loop with nothing damping it.
    // Cutting the camera off the skeleton is what breaks that loop.
    [SerializeField] private Transform cameraPivot;
    [SerializeField] private float mouseSensitivity = 0.1f;
    [SerializeField] private float maxLookAngle = 85f;

    [Header("Head Follow")]
    // The socket on the head bone. The view follows where the animation puts it,
    // but only slowly: a low-pass on the skeleton rather than a hard mount to it.
    //
    // Crouching, climbing, leaning into a car -- all of it is already in the
    // clips, and following the bone is how that reaches the view without anyone
    // restating it in code. What the clips also carry is per-frame stride jitter,
    // and that is what the damping is for. Both live at different frequencies, so
    // one filter separates them: slow gestures pass, shake does not.
    [SerializeField] private Transform headAnchor;

    // Where the eyes sit relative to that socket is the scene's to decide: the
    // pivot's offset from the head is measured once at startup from wherever it
    // has been dragged to, so it can be placed by eye in the viewport rather than
    // typed in. Edit it in Edit mode -- nudging it mid-play does nothing.
    //
    // 0 pins the view to the capsule and ignores the skeleton entirely; 1 follows
    // the head in full. Between the two it follows part of the way, which is the
    // usual answer for a walk cycle with more shoulder in it than the view wants.
    [SerializeField, Range(0f, 1f)] private float headFollowAmount = 1f;

    // Roughly how long the view takes to catch up, in seconds -- for the follow
    // above and the crouch drop below alike. This is the whole filter: too short
    // and the stride comes through, too long and a crouch turns into a slow sink.
    // A few tenths is the usual band.
    [SerializeField] private float headFollowSmoothTime = 0.35f;

    // An extra drop while crouched, on top of whatever the crouch clip already
    // gives, in metres. Still applied on its own rather than folded into the
    // follow target, so it lands in full even at a headFollowAmount of 0 -- the
    // two are separate systems and turning one off should not take the other with
    // it. Leave at 0 if the clip's own drop is enough.
    [SerializeField] private float crouchEyeDrop = 0.2f;

    [Header("Peek")]
    // A sideways slide, in metres. Nothing bends and nothing rolls: the view and
    // everything under it -- the weapon, the aim target -- step out to the side as
    // one rigid piece and step back.
    //
    // Deliberately not a lean. A lean has to be built twice, once as a spine bend
    // and once as camera geometry, and two constructions of the same motion can be
    // made to look alike but never to agree: the gap between them is a weapon
    // hanging off the camera while the shoulders go somewhere else, and that gap
    // is the hands coming off the grips. A slide has nothing to disagree with.
    [SerializeField] private float peekDistance = 0.35f;

    // Degrees of roll on top of that slide. One value covers the view and the
    // weapon both, because the weapon hangs off the pivot -- rolling the pivot
    // rolls the pair as one rigid piece, and there is no second construction of
    // it to drift out of step. The body stays level under them, which is the
    // trade for that: a small angle reads as the head cocking with the lean, a
    // large one as shoulders that forgot to come along.
    [SerializeField] private float peekTilt = 6f;

    // How much of the slide is covered per second: 5 reaches full in a fifth of a
    // second. Constant-rate rather than damped so leaning out and back takes the
    // same time either way, which is what makes it something a player can count on
    // under fire.
    [SerializeField] private float peekSpeed = 5f;

    [Header("Strafe Tilt")]
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private float tiltAmount = 3f;
    [SerializeField] private float tiltSpeed = 8f;

    [Header("Aim")]
    // The camera that actually draws the frame -- the one carrying the Brain, not
    // the vcam and not the pivot. Optional: left empty it finds Camera.main, so
    // this can't be the thing that silently isn't wired.
    [SerializeField] private Camera renderCamera;

    [Header("Speed FOV")]
    [SerializeField] private CinemachineCamera cinemachineCamera;
    [SerializeField] private float maxFovBoost = 10f;
    [SerializeField] private float fovSpeed = 8f;

    [Header("Ladder Look")]
    [SerializeField] private float climbLookYawLimit = 100f;
    [SerializeField] private float climbLookPitchUpLimit = 60f;
    [SerializeField] private float climbLookPitchDownLimit = 60f;

    [Header("Car Look")]
    [SerializeField] private float carLookYawLimitRight = 60f;
    [SerializeField] private float carLookYawLimitLeft = 60f;
    [SerializeField] private float carLookPitchUpLimit = 30f;
    [SerializeField] private float carLookPitchDownLimit = 30f;
    [SerializeField, Range(0f, 1f)] private float carPitchLimitAtMaxYawRatio = 0.2f;

    [Header("Camera Breathing")]
    [SerializeField] private float breathFrequency = 1f;
    [SerializeField] private float breathPitchAmount = 0.3f;
    [SerializeField] private float breathYawAmount = 0.3f;
    [SerializeField] private float breathRollAmount = 0.3f;
    [SerializeField] private float breathSmoothing = 4f;

    [Header("Camera Bob")]
    // The bob's cadence comes from the legs, through PlayerAnimator's own read of
    // the locomotion clip, rather than from a frequency set here. A number tuned to
    // roughly match the walk cycle is the one thing that can never work: it is
    // right at exactly one speed and drifts in and out of phase everywhere else,
    // and the view rocking against the footfall instead of with it is worse than no
    // bob at all.
    //
    // Left empty the bob falls back to its own clock at bobFallbackFrequency, which
    // is the old behaviour and is only there so this component still does something
    // on its own.
    [SerializeField] private PlayerAnimator playerAnimator;

    // Degrees of head start, for lining the bob's low point up with the footfall.
    // Which frame of the clip a foot lands on is the animation's business and there
    // is no reading it from here, so it is dialled in by eye once.
    [SerializeField] private float bobPhaseOffset;

    [SerializeField] private float bobFallbackFrequency = 6f;
    [SerializeField] private float bobPitchAmount = 0.5f;
    [SerializeField] private float bobYawAmount = 0.5f;
    [SerializeField] private float bobRollAmount = 0.5f;

    // Amounts above are at a full walk; the character's actual speed scales them.
    // Not the rate any more -- the clip decides that, and a run clip is already
    // faster than a walk one without anyone multiplying anything.
    //
    // Clamped because the ratio has no ceiling of its own: a vehicle or a future
    // gait could hand it anything.
    [SerializeField] private float maxBobSpeedRatio = 2f;
    [SerializeField] private float bobSmoothing = 8f;

    [Header("Camera Jump / Land Shake")]
    // The view's alone -- none of this reaches the weapon. Leaving the ground and
    // hitting it again are things that happen to the head; the hands are holding
    // something braced and go on holding it. It lands on the rendered camera rather
    // than the pivot, which is the one transform under the rig the items do not
    // hang off, so there is nothing to take back out afterwards.
    //
    // Pitch is the drop and the recovery; roll is the head not landing perfectly
    // square, and rolls the same way every time so a landing is a thing the player
    // can recognise rather than a different jolt each time.
    [SerializeField] private float jumpShakeAmount = 2f;
    [SerializeField] private float landShakeAmount = 4f;
    [SerializeField] private float jumpShakeRollAmount = 1f;
    [SerializeField] private float landShakeRollAmount = 2f;
    [SerializeField] private float shakeSpring = 200f;
    [SerializeField] private float shakeDamping = 20f;


    // The rendered camera is a zero-offset child of the pivot, so this is the eye
    // point as well as the pivot -- what a weapon should trace a shot along.
    public Transform CameraTransform => cameraPivot;

    // Dead centre of the rendered image, as a world ray. Asked of the camera's own
    // projection rather than built from some transform's forward, so it is the
    // crosshair by definition -- under any field of view, aspect, lens shift or
    // Cinemachine arrangement, and whether or not the vcam sits on the pivot.
    //
    // Every transform in the chain is a guess at where the picture is pointing.
    // The camera that draws the picture is not a guess.
    public Ray AimRay
    {
        get
        {
            Camera camera = renderCamera != null ? renderCamera : Camera.main;

            return camera != null
                ? camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f))
                : new Ray(cameraPivot.position, cameraPivot.forward);
        }
    }
    public float Pitch { get; private set; }
    public float YawDelta { get; private set; }

    // How far the view actually turned this frame, in degrees -- x yaw, y pitch.
    // Applied rather than requested: against a pitch limit or a car's yaw stop the
    // mouse keeps moving but the view does not, and anything following the view
    // has to stop with it or it will lean into a wall that isn't there. Distinct
    // from YawDelta, which is the body's share of the turn and is zero on a ladder
    // or in a car even while the view is still swinging.
    public Vector2 LookDelta { get; private set; }

    // How far into a lean the view currently is: -1 fully left, 0 upright, 1
    // fully right, already smoothed. The camera and everything parented under it
    // -- the weapon, the aim target -- lean as one rigid piece, so this exists for
    // the one part that can't: the torso, which has to be bent by the Animator or
    // the rig to match. Read it, don't drive it.
    public float PeekAmount => _currentPeek;

    // The same lean in metres rather than as a fraction: how far sideways the view
    // has actually stepped. The body slides by this too, so it is read rather than
    // restated -- a second copy of the distance is a second thing to keep in step,
    // and the two drifting apart is the weapon going one way and the shoulders
    // another.
    public float PeekOffset => _currentPeek * peekDistance;

    // And the roll, in degrees, signed as the pivot actually applies it. The head
    // bone is turned by this so the silhouette cocks with the view instead of
    // staying square while the camera leans -- which is the whole of what a lean
    // reads as from outside, and the only part of it a shadow can show.
    public float PeekTiltAngle => -_currentPeek * peekTilt;

    // The walk cycle's phase, in radians, offset to wherever the footfall sits.
    // Normally the locomotion clip's own, so the view, the hands and the legs are
    // three readings of one number rather than three approximations of each other.
    //
    // Exposed so HandMotion drives the hands off the identical value. Matching
    // frequencies is not enough and never was: two clocks at the same nominal rate
    // still drift, and the drift is exactly the thing that reads as wrong.
    public float BobPhase => _bobPhase;

    // The idle drift's phase, in radians, advancing only while the character is
    // standing still. Exposed for the same reason the bob's is: the hands breathe
    // off this rather than a clock of their own, so the two are one breath rather
    // than two at nearly the same rate drifting through each other.
    public float BreathPhase => _breathTimer;

    private InputAction _lookAction;
    private Vector2 _lookInput;
    private float _currentTilt;
    private float _baseFov;
    private float _climbCameraYaw;
    private bool _wasClimbing;
    private float? _fovOverride;
    private float _breathTimer;
    private Vector3 _currentBreathRotation;
    private float _bobTimer;
    private float _bobPhase;
    private Quaternion _cameraBaseLocalRotation;
    private Vector3 _currentBobRotation;
    private float _shakeOffset;
    private float _shakeVelocity;
    private float _shakeRollOffset;
    private float _shakeRollVelocity;
    private Vector3 _shoulderingPositionOffset;
    private Vector3 _shoulderingPositionVelocity;
    private Vector3 _shoulderingRotationOffset;
    private Vector3 _shoulderingRotationVelocity;
    private float _shoulderingSpring = 220f;
    private float _shoulderingDamping = 22f;
    private Vector3 _cameraBaseLocalPosition;
    private float _fireKickOffset;
    private float _fireKickVelocity;
    private float _fireKickYawOffset;
    private float _fireKickYawVelocity;
    private float _fireKickSpring = 200f;
    private float _fireKickDamping = 20f;
    private float _rollShakeOffset;
    private float _rollShakeVelocity;
    private float _rollShakeSpring = 200f;
    private float _rollShakeDamping = 20f;
    private Vector3 _basePivotLocalPosition;
    private Vector3 _pivotOffsetFromHead;
    private Vector3 _followedLocalPosition;
    private Vector3 _followVelocity;
    private float _crouchDrop;
    private float _crouchDropVelocity;
    private InputAction _peekAction;
    private float _currentPeek;

    // Lets an equipped item (e.g. Weapon) override FOV while it's active,
    // without PlayerLook needing to know anything about items -- same
    // push-values-in pattern as PlayerAnimator's hand IK targets.
    public void SetFovOverride(float fov) => _fovOverride = fov;
    public void ClearFovOverride() => _fovOverride = null;

    // Weapon-driven recoil kick on the camera itself -- the weapon owns the
    // amount/spring/damping (its recoil "feel") and just pushes them in,
    // same push-values-in pattern as the FOV override above.
    public void SetFireKickProfile(float spring, float damping)
    {
        _fireKickSpring = spring;
        _fireKickDamping = damping;
    }

    // Roll shake has its own spring/damping, independent from the pitch/yaw
    // kick above -- a cosmetic rattle on top of the deterministic punch.
    public void SetRollShakeProfile(float spring, float damping)
    {
        _rollShakeSpring = spring;
        _rollShakeDamping = damping;
    }

    // Modern CoD-style recoil kick: a deterministic upward pitch punch plus a
    // random left/right yaw punch per shot, plus a roll shake with its own
    // spring/damping -- all settle back independently. Velocities are set, not
    // added, so rapid fire can't stack shots into a runaway kick.
    //
    // The roll is the one part that doesn't get scattered: it is the weapon's
    // own cant, the same on every shot, so its direction comes from the sign of
    // the amount rather than a coin flip. Only the yaw is random, which is what
    // keeps a burst from walking predictably to one side.
    // The view's share of a shouldering jolt, fired by HandMotion at the same moment
    // as the hands' own so the two are one event rather than two that happen to
    // coincide. Everything about it -- the impulses and the spring it settles on --
    // is pushed in from there, because a stance change is one motion and splitting
    // its tuning across two components is how the halves start disagreeing.
    //
    // Lands on the rendered camera rather than the pivot, which is what keeps it off
    // the weapon: the hands have their own version of this and would otherwise take
    // both.
    public void AddShoulderingKick(Vector3 positionImpulse, Vector3 rotationImpulse, float spring, float damping)
    {
        _shoulderingPositionVelocity = positionImpulse;
        _shoulderingRotationVelocity = rotationImpulse;
        _shoulderingSpring = spring;
        _shoulderingDamping = damping;
    }

    public void AddFireKick(float kickAmount, float horizontalKickAmount, float rollShakeAmount)
    {
        _fireKickVelocity = -kickAmount;
        _fireKickYawVelocity = Random.Range(-1f, 1f) * horizontalKickAmount;
        _rollShakeVelocity = rollShakeAmount;
    }

    // The head socket in whatever space the pivot's localPosition is written in.
    // Taken from the pivot's actual parent rather than assuming it is this object,
    // so the rig can be nested a level deeper without this quietly reading the
    // wrong space and putting the eyes somewhere off in the world.
    private Vector3 HeadLocalPosition
    {
        get
        {
            Transform pivotSpace = cameraPivot.parent != null ? cameraPivot.parent : transform;
            return pivotSpace.InverseTransformPoint(headAnchor.position);
        }
    }

    private void Awake()
    {
        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        _lookAction = playerMap.FindAction("Look");
        _peekAction = playerMap.FindAction("Peek");

        if (cameraPivot != null)
        {
            _basePivotLocalPosition = cameraPivot.localPosition;
            _followedLocalPosition = _basePivotLocalPosition;

            // Measured rather than typed: the pivot is dragged into place against
            // the head in the viewport, and the gap between the two right then is
            // what it keeps. Read before the Animator has posed anything, so this
            // is the bind pose -- a fraction off the idle pose, which the damping
            // below absorbs over the first moments of play.
            if (headAnchor != null)
                _pivotOffsetFromHead = _basePivotLocalPosition - HeadLocalPosition;
        }

        if (cinemachineCamera != null)
        {
            _baseFov = cinemachineCamera.Lens.FieldOfView;
            _cameraBaseLocalPosition = cinemachineCamera.transform.localPosition;
            _cameraBaseLocalRotation = cinemachineCamera.transform.localRotation;
        }
    }

    private void OnEnable()
    {
        _lookAction.Enable();
        _peekAction?.Enable();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void OnDisable()
    {
        _lookAction.Disable();
        _peekAction?.Disable();
    }

    private void Update()
    {
        _lookInput = _lookAction.ReadValue<Vector2>();
        ApplyLook();
    }

    private void ApplyLook()
    {
        bool isClimbing = movement != null && movement.IsClimbingLadder;
        bool isInCar = movement != null && movement.IsInCar;
        bool lockBodyYaw = isClimbing || isInCar;

        if (_wasClimbing && !lockBodyYaw)
        {
            transform.Rotate(Vector3.up * _climbCameraYaw);
            _climbCameraYaw = 0f;
        }
        _wasClimbing = lockBodyYaw;

        float yaw = _lookInput.x * mouseSensitivity;

        float appliedYaw;

        if (lockBodyYaw)
        {
            float yawLimitLeft = isInCar ? carLookYawLimitLeft : climbLookYawLimit;
            float yawLimitRight = isInCar ? carLookYawLimitRight : climbLookYawLimit;

            // The view still turns here, it just turns without the body: the yaw
            // goes into the camera's own offset instead of the capsule. YawDelta
            // stays zero because that is the body's, but the look itself moved.
            float previousClimbCameraYaw = _climbCameraYaw;
            _climbCameraYaw = Mathf.Clamp(_climbCameraYaw + yaw, -yawLimitLeft, yawLimitRight);
            appliedYaw = _climbCameraYaw - previousClimbCameraYaw;
            YawDelta = 0f;
        }
        else
        {
            transform.Rotate(Vector3.up * yaw);
            YawDelta = yaw;
            appliedYaw = yaw;
        }

        float pitchUpLimit = isInCar ? carLookPitchUpLimit : (isClimbing ? climbLookPitchUpLimit : maxLookAngle);
        float pitchDownLimit = isInCar ? carLookPitchDownLimit : (isClimbing ? climbLookPitchDownLimit : maxLookAngle);

        // The further the camera has turned toward its yaw limit in the car, the less
        // pitch freedom it has left -- mirrors how far you can actually tilt your head
        // up/down once you've already twisted your neck near its rotational limit.
        if (isInCar)
        {
            float yawLimitForRatio = _climbCameraYaw >= 0f ? carLookYawLimitRight : carLookYawLimitLeft;
            float yawRatio = yawLimitForRatio > 0f ? Mathf.Clamp01(Mathf.Abs(_climbCameraYaw) / yawLimitForRatio) : 0f;
            float pitchScale = Mathf.Lerp(1f, carPitchLimitAtMaxYawRatio, yawRatio);
            pitchUpLimit *= pitchScale;
            pitchDownLimit *= pitchScale;
        }

        float previousPitch = Pitch;
        Pitch = Mathf.Clamp(Pitch - _lookInput.y * mouseSensitivity, -pitchUpLimit, pitchDownLimit);

        LookDelta = new Vector2(appliedYaw, Pitch - previousPitch);

        float targetTilt = movement != null && !movement.IsMovementLocked
            ? -movement.MoveInput.x * tiltAmount
            : 0f;
        _currentTilt = Mathf.Lerp(_currentTilt, targetTilt, tiltSpeed * Time.deltaTime);

        bool isMoving = movement != null && movement.IsGroundedStable && !movement.IsMovementLocked && movement.MoveInput.sqrMagnitude > 0.01f;

        // Purely rotational -- a slow sine drift on pitch/yaw/roll, no position
        // offset, so the camera never feels perfectly locked while standing
        // still. Only while standing still, though -- bob (below) takes over
        // once moving instead of the two stacking.
        if (!isMoving)
            _breathTimer += Time.deltaTime * breathFrequency;

        Vector3 targetBreathRotation = !isMoving
            ? new Vector3(
                Mathf.Sin(_breathTimer * 0.5f) * breathPitchAmount,
                Mathf.Sin(_breathTimer) * breathYawAmount,
                Mathf.Cos(_breathTimer) * breathRollAmount)
            : Vector3.zero;
        _currentBreathRotation = Vector3.Lerp(_currentBreathRotation, targetBreathRotation, breathSmoothing * Time.deltaTime);

        // Also purely rotational -- speeds up and grows with movement state
        // (crouch slower/smaller, sprint faster/bigger). Pitch on the doubled
        // frequency; yaw and roll on the base frequency, 90 degrees apart from
        // each other for a natural circular swing (same pairing as the weapon's
        // own hand bob).
        float bobSpeedRatio = movement != null
            ? Mathf.Min(movement.GaitSpeedRatio, maxBobSpeedRatio)
            : 1f;

        // Only advanced when nothing better is available. With an animator the
        // phase is the clip's, and a clock running alongside it would be a second
        // opinion nobody asked for.
        if (playerAnimator == null && isMoving)
            _bobTimer += Time.deltaTime * bobFallbackFrequency * bobSpeedRatio;

        _bobPhase = playerAnimator != null
            ? playerAnimator.LocomotionPhase + bobPhaseOffset * Mathf.Deg2Rad
            : _bobTimer;

        Vector3 targetBobRotation = isMoving
            ? new Vector3(
                Mathf.Sin(_bobPhase * 2f) * bobPitchAmount * bobSpeedRatio,
                Mathf.Sin(_bobPhase) * bobYawAmount * bobSpeedRatio,
                Mathf.Cos(_bobPhase) * bobRollAmount * bobSpeedRatio)
            : Vector3.zero;
        _currentBobRotation = Vector3.Lerp(_currentBobRotation, targetBobRotation, bobSmoothing * Time.deltaTime);

        // Damped spring kick on jump (up) and landing (down) -- an impulse on
        // velocity snaps it away and settles back like a real spring. Pitch and roll
        // get an offset each but share the spring, so the two settle together and a
        // landing reads as one motion rather than two arriving at their own pace.
        if (movement != null && movement.JumpedThisFrame)
        {
            _shakeVelocity -= jumpShakeAmount;
            _shakeRollVelocity -= jumpShakeRollAmount;
        }

        if (movement != null && movement.LandedThisFrame)
        {
            _shakeVelocity += landShakeAmount;
            _shakeRollVelocity += landShakeRollAmount;
        }

        _shakeVelocity += (-shakeSpring * _shakeOffset - shakeDamping * _shakeVelocity) * Time.deltaTime;
        _shakeOffset += _shakeVelocity * Time.deltaTime;

        _shakeRollVelocity += (-shakeSpring * _shakeRollOffset - shakeDamping * _shakeRollVelocity) * Time.deltaTime;
        _shakeRollOffset += _shakeRollVelocity * Time.deltaTime;

        // Shouldering -- impulse and spring both pushed in by HandMotion when a
        // stance changes, so the view settles on exactly the terms the hands do.
        _shoulderingPositionVelocity += (-_shoulderingSpring * _shoulderingPositionOffset
            - _shoulderingDamping * _shoulderingPositionVelocity) * Time.deltaTime;
        _shoulderingPositionOffset += _shoulderingPositionVelocity * Time.deltaTime;

        _shoulderingRotationVelocity += (-_shoulderingSpring * _shoulderingRotationOffset
            - _shoulderingDamping * _shoulderingRotationVelocity) * Time.deltaTime;
        _shoulderingRotationOffset += _shoulderingRotationVelocity * Time.deltaTime;

        // Weapon-driven recoil kick -- spring/damping come from whatever item is
        // equipped (pushed via SetFireKickProfile), impulse from AddFireKick per shot.
        _fireKickVelocity += (-_fireKickSpring * _fireKickOffset - _fireKickDamping * _fireKickVelocity) * Time.deltaTime;
        _fireKickOffset += _fireKickVelocity * Time.deltaTime;

        _fireKickYawVelocity += (-_fireKickSpring * _fireKickYawOffset - _fireKickDamping * _fireKickYawVelocity) * Time.deltaTime;
        _fireKickYawOffset += _fireKickYawVelocity * Time.deltaTime;

        _rollShakeVelocity += (-_rollShakeSpring * _rollShakeOffset - _rollShakeDamping * _rollShakeVelocity) * Time.deltaTime;
        _rollShakeOffset += _rollShakeVelocity * Time.deltaTime;

        if (cameraPivot != null)
        {
            // Critically damped rather than lerped: a spring that never overshoots,
            // so a crouch settles onto its new height instead of dipping past it
            // and coming back. smoothTime is then an honest "how long to catch up"
            // rather than a rate whose meaning changes with the distance.
            Vector3 followTarget = headAnchor != null
                ? HeadLocalPosition + _pivotOffsetFromHead
                : _basePivotLocalPosition;

            _followedLocalPosition = Vector3.SmoothDamp(
                _followedLocalPosition, followTarget, ref _followVelocity, headFollowSmoothTime);

            // Rebuilt from the scene's base every frame rather than nudged from
            // where it was, so nothing can accumulate an offset here over time.
            // The head is the only thing allowed to move the view at all: every
            // other motion it has -- bob, breath, kick -- is rotational and goes
            // in below, where no amount of it can shift the eye point.
            float targetCrouchDrop = movement != null && movement.IsCrouching ? crouchEyeDrop : 0f;
            _crouchDrop = Mathf.SmoothDamp(
                _crouchDrop, targetCrouchDrop, ref _crouchDropVelocity, headFollowSmoothTime);

            // Subtracted after the follow blend rather than folded into its target,
            // so it applies in full even at a headFollowAmount of 0 -- the two are
            // separate systems and turning one off should not take the other with
            // it.
            Vector3 pivotPosition = Vector3.Lerp(
                _basePivotLocalPosition, _followedLocalPosition, headFollowAmount)
                - Vector3.up * _crouchDrop;

            // Nowhere to step out to while a ladder or a car has the character: the
            // axis is still being read there and would slide the view off a body
            // that has no way to follow it.
            float targetPeek = _peekAction != null && (movement == null || !movement.IsMovementLocked)
                ? Mathf.Clamp(_peekAction.ReadValue<float>(), -1f, 1f)
                : 0f;
            _currentPeek = Mathf.MoveTowards(_currentPeek, targetPeek, peekSpeed * Time.deltaTime);

            // Sideways in the capsule's own frame, so the slide follows the body
            // rather than the pitch -- stepping out while looking up should still
            // step out sideways, not up and over.
            pivotPosition += Vector3.right * (_currentPeek * peekDistance);

            cameraPivot.localPosition = pivotPosition;

            // Pre-multiplied, so the roll is about the body's forward rather than
            // the view's. Folded into the Euler's Z instead it would roll about
            // wherever the camera happened to be pointing -- peek while looking at
            // your feet and the view would spin rather than cock to the side.
            //
            // Same sign as the strafe tilt below, so stepping right and leaning
            // right cock the same way instead of cancelling.
            Quaternion peekTiltRotation = Quaternion.AngleAxis(-_currentPeek * peekTilt, Vector3.forward);

            // The fire kick is split between here and the camera below, along the
            // line of what a shot actually does to a braced weapon.
            //
            // Pitch and yaw belong here, on the pivot, so the weapon rides them: the
            // muzzle climbing and wandering off target is the recoil, and a weapon
            // that stayed put through it would be a weapon with none.
            //
            // Its roll does not, and the jump and landing shake does not at all --
            // both are below. What is left here is the look, the breath, the bob and
            // the recoil the weapon should share.
            cameraPivot.localRotation = peekTiltRotation * Quaternion.Euler(
                Pitch + _currentBreathRotation.x + _currentBobRotation.x + _fireKickOffset,
                _climbCameraYaw + _currentBreathRotation.y + _currentBobRotation.y + _fireKickYawOffset,
                _currentTilt + _currentBreathRotation.z + _currentBobRotation.z);
        }

        if (cinemachineCamera != null)
        {
            // Everything the weapon should have no part in, on the rendered camera --
            // the one thing under the pivot the items are not parented to. A shot's
            // roll, because it rocks the head rather than spinning the weapon about
            // its own barrel; and the whole jump and landing shake, because leaving
            // the ground and hitting it again happen to the head while the hands go
            // on holding what they were holding.
            //
            // Rebuilt from the rotation the camera was placed at rather than nudged
            // from where it was left, so a shake that never quite settles can't walk
            // the camera off over a magazine's worth of shots.
            cinemachineCamera.transform.localPosition = _cameraBaseLocalPosition + _shoulderingPositionOffset;

            cinemachineCamera.transform.localRotation = _cameraBaseLocalRotation
                * Quaternion.Euler(_shakeOffset, 0f, _rollShakeOffset + _shakeRollOffset)
                * Quaternion.Euler(_shoulderingRotationOffset);
        }

        if (cinemachineCamera != null)
        {
            float fovBoostRatio = 0f;
            if (movement != null)
            {
                if (movement.IsSprintingStable)
                    fovBoostRatio = 1f;
                else if (isInCar)
                    fovBoostRatio = movement.CarSpeedRatio;
            }

            LensSettings lens = cinemachineCamera.Lens;
            float targetFov = _fovOverride ?? (_baseFov + maxFovBoost * fovBoostRatio);
            lens.FieldOfView = Mathf.Lerp(lens.FieldOfView, targetFov, fovSpeed * Time.deltaTime);
            cinemachineCamera.Lens = lens;
        }
    }
}

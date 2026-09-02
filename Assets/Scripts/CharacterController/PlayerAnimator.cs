using UnityEngine;
using UnityEngine.Animations.Rigging;

[RequireComponent(typeof(Animator))]
[DefaultExecutionOrder(50)]
public class PlayerAnimator : MonoBehaviour
{
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private PlayerLook look;
    [SerializeField] private PlayerItems items;
    [SerializeField] private float turnSpeed = 720f;
    [SerializeField] private float turnInPlaceToleranceAngle = 90f;

    [Header("Aim Rig")]
    [SerializeField] private MultiAimConstraint spineAim;
    [SerializeField] private MultiAimConstraint chestAim;
    [SerializeField] private MultiAimConstraint upperChestAim;
    [SerializeField] private MultiAimConstraint neckAim;

    [Header("Peek Slide")]
    // The body's share of the lean. PlayerLook steps the view sideways; these three
    // carry the torso the same distance so the model goes with it instead of the
    // camera sliding out of a body left standing where it was.
    //
    // Split equally between them rather than dropped on one joint. They sit in a
    // chain, so each one's share carries everything above it: a third at the spine,
    // a third more at the chest, a third more again at the upper chest, and the
    // shoulders arrive at the full distance having bent through three joints rather
    // than sheared at one.
    [SerializeField] private Transform peekSpineBone;
    [SerializeField] private Transform peekChestBone;
    [SerializeField] private Transform peekUpperChestBone;

    // Rolled with the lean, so the head cocks rather than riding it out square.
    // Nothing in first person can see this -- it is for the silhouette, and the
    // shadow is where a lean either reads or doesn't.
    [SerializeField] private Transform peekHeadBone;

    // Scales that roll against the view's. 1 matches the camera exactly, which is
    // the honest answer for a head the camera is nominally inside; anything else
    // is for the shadow's sake, where the head reads small and a little more cock
    // than the view took can be what makes the pose legible from outside.
    [SerializeField] private float peekHeadTiltMultiplier = 1f;

    [Header("Hand IK")]
    [SerializeField] private float handIKTransitionDuration = 0.08f;

    [Header("Layer Weights")]
    [SerializeField] private float layerWeightTransitionSpeed = 8f;

    // State names, not parameter names -- the turn recovery reads the animator's
    // progress through these states, and the clips inside them are called
    // something else entirely. All four are here because the same TurnLeft and
    // TurnRight triggers reach standing and crouching turns alike: which one
    // plays depends on whether the animator is in Locomotion or CrouchLocomotion.
    private static readonly string[] TurnStateNames =
    {
        "TurnLeft", "TurnRight", "CrouchTurnLeft", "CrouchTurnRight"
    };

    private const int BaseLayerIndex = 0;

    // Input below this reads as no input at all -- the same figure the movement
    // and animation code already used in several places, named once instead.
    private const float MoveThreshold = 0.05f;

    // Metres per second, not the 0-1 input figure: below this the character has
    // effectively stopped, whatever is or isn't being pressed. The two are
    // separate because acceleration pulled them apart -- there is now a stretch
    // at either end of a move where one is zero and the other isn't.
    private const float SpeedThreshold = 0.1f;

    // The mount over the top edge has no clip of its own: it is the dismount
    // played backwards, which is why both live in the same state. PlayerMovement
    // is what inverts the progress for that case -- the reversal is content, and
    // stays in one place rather than spreading through the flow.
    private const string LadderMountStateName = "LadderClimbEnter";
    private const string LadderDismountStateName = "LadderClimbFinish";

    // True from the moment a turn-in-place is asked for until the body has come
    // all the way back round. The standing and crouching turns are separate
    // states with no transition between them, so a crouch pressed during one
    // doesn't reach the model at all -- the legs finish the turn they started,
    // standing. Anything that would otherwise drop the player into a crouch the
    // model isn't taking can gate on this and wait its turn out.
    public bool IsTurningInPlace => _isRecoveringFromTurn;

    // How far through its current clip the base layer is, in radians -- one full
    // stride to a turn of 2 pi.
    //
    // This is the walk cycle. Not an approximation of it, not a sine at roughly the
    // right frequency: the actual clip, at whatever rate the blend tree is playing
    // it, which is what decides when a foot lands. Anything that should agree with
    // the footfall -- the camera's bob, the hands' -- reads this instead of running
    // a clock of its own, because two clocks at nearly the same rate are worse than
    // useless. They drift into and out of phase, and the walk looks wrong in a way
    // no amount of retuning either one fixes.
    public float LocomotionPhase { get; private set; }

    private static readonly int ForwardAmountHash = Animator.StringToHash("ForwardAmount");
    private static readonly int IsCrouchingHash = Animator.StringToHash("IsCrouching");
    private static readonly int TurnLeftHash = Animator.StringToHash("TurnLeft");
    private static readonly int TurnRightHash = Animator.StringToHash("TurnRight");
    private static readonly int IsMovingHash = Animator.StringToHash("IsMoving");
    private static readonly int IsClimbingLadderHash = Animator.StringToHash("IsClimbingLadder");
    private static readonly int ClimbSpeedHash = Animator.StringToHash("ClimbSpeed");
    private static readonly int LadderEnterHash = Animator.StringToHash("LadderEnter");
    private static readonly int LadderFinishHash = Animator.StringToHash("LadderFinish");
    private static readonly int LadderTransitionSpeedHash = Animator.StringToHash("LadderTransitionSpeed");
    private static readonly int LadderEnterFromTopHash = Animator.StringToHash("LadderEnterFromTop");
    private static readonly int LadderMountCompleteHash = Animator.StringToHash("LadderMountComplete");
    private static readonly int LadderExitBottomHash = Animator.StringToHash("LadderExitBottom");
    private static readonly int IsInCarHash = Animator.StringToHash("IsInCar");
    private static readonly int CarTransitionSpeedHash = Animator.StringToHash("CarTransitionSpeed");
    private static readonly int CarEnterHash = Animator.StringToHash("CarEnter");
    private static readonly int CarExitHash = Animator.StringToHash("CarExit");
    private static readonly int CarEnterCompleteHash = Animator.StringToHash("CarEnterComplete");
    private static readonly int IsAimingHash = Animator.StringToHash("IsAiming");

    private Animator _animator;
    private int _itemLayerIndex;
    private int _ladderCarLayerIndex;
    private float _ladderCarLayerWeight;
    private float _facingOffset;
    private bool _isRecoveringFromTurn;
    private bool _hasEnteredTurnState;
    private float _turnStartWaitTimer;
    private float _turnStartOffset;
    private bool _isSquaringUpForEntry;
    private float _entryStartFacingOffset;
    private bool _isItemPoseHeld;
    private bool _hasAimRigWeightOverride;
    private bool _wasAimRigWeightOverrideActive;
    private float _spineAimWeightOverride;
    private float _chestAimWeightOverride;
    private float _upperChestAimWeightOverride;
    private float _neckAimWeightOverride;
    private float _spineAimPreOverrideWeight;
    private float _chestAimPreOverrideWeight;
    private float _upperChestAimPreOverrideWeight;
    private float _neckAimPreOverrideWeight;
    private Transform _leftHandIKTarget;
    private Transform _rightHandIKTarget;
    private Transform _leftHandIKHint;
    private Transform _rightHandIKHint;
    private float? _leftHandIKTransitionDurationOverride;
    private float? _rightHandIKTransitionDurationOverride;
    private readonly HandIKState _leftHandIKState = new HandIKState();
    private readonly HandIKState _rightHandIKState = new HandIKState();

    private class HandIKState
    {
        public Transform PreviousTarget;
        public Vector3 CurrentPosition;
        public Quaternion CurrentRotation;
        public Transform BlendReference;
        public Vector3 BlendStartLocalPosition;
        public Quaternion BlendStartLocalRotation;
        public float BlendT = 1f;
        public float TransitionDuration;
    }

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _itemLayerIndex = _animator.GetLayerIndex("Item Layer");
        _ladderCarLayerIndex = _animator.GetLayerIndex("LadderCar");
    }

    private void Update()
    {
        // Read before anything is written, so it is the pose the frame was actually
        // drawn with rather than one the parameters below have already moved on
        // from. normalizedTime keeps counting up across loops, which trigonometry
        // wraps on its own -- there is nothing to reset and so nothing to jump.
        LocomotionPhase = GetEffectiveStateInfo(BaseLayerIndex).normalizedTime * Mathf.PI * 2f;

        _animator.SetBool(IsClimbingLadderHash, movement.IsClimbingLadder);
        _animator.SetFloat(ClimbSpeedHash, movement.IsClimbingLadder ? movement.MoveInput.y : 0f);
        _animator.SetBool(IsInCarHash, movement.IsInCar);

        ApplyItemPose();

        if (_ladderCarLayerIndex >= 0)
        {
            // Held down over the slide to the entry point so the walk on the base
            // layer is what shows while the character is still on its way there.
            // Only the weight is held: the flags above have to stay true the whole
            // time, because the ladder and car states exit on them going false --
            // gate those and the layer drops straight back to Idle and never
            // finds its way back in.
            bool hasArrived = !movement.IsSlidingToEntry;
            float targetLadderCarWeight = (movement.IsClimbingLadder || movement.IsInCar) && hasArrived ? 1f : 0f;
            _ladderCarLayerWeight = Mathf.Lerp(_ladderCarLayerWeight, targetLadderCarWeight, layerWeightTransitionSpeed * Time.deltaTime);
            _animator.SetLayerWeight(_ladderCarLayerIndex, _ladderCarLayerWeight);
        }

        UpdateAimRigWeightOverride();

        if (movement.IsClimbingLadder || movement.IsInCar)
        {
            if (!_isSquaringUpForEntry)
            {
                _isSquaringUpForEntry = true;
                _entryStartFacingOffset = _facingOffset;
            }

            // Paced by the slide to the entry point, not by a rate of its own.
            // The root is rotating to face the ladder or car over exactly that
            // span, so sharing its progress is what makes the two land together
            // as a single turn instead of two overlapping ones.
            _facingOffset = Mathf.LerpAngle(_entryStartFacingOffset, 0f, movement.EntrySlideProgress);
            transform.localRotation = Quaternion.Euler(0f, _facingOffset, 0f);

            // Walk over the slide rather than gliding to the door, at a full
            // stride throughout -- 1 is the top of the walk half of the blend
            // tree, 2 being a run. Deliberately not scaled to the distance being
            // covered: a partial blend over a short hop reads as trudging, and
            // the slide is brief enough either way that a full stride sells it.
            // Set outright rather than ramped: the slide is a lerp over a fixed
            // duration, so it really is at full speed from its first frame. There
            // is nothing to accelerate through.
            float entryForwardAmount = movement.IsSlidingToEntry ? 1f : 0f;

            _animator.SetFloat(ForwardAmountHash, entryForwardAmount);
            _animator.SetBool(IsMovingHash, entryForwardAmount > MoveThreshold);
            _animator.SetBool(IsCrouchingHash, false);
            return;
        }

        _isSquaringUpForEntry = false;

        // Clamped the way ApplyMovement clamps its own direction, and for the same
        // reason: the Move binding is a Digital composite rather than a Digital
        // Normalized one, so a diagonal reads 1.41 where a straight press reads 1.
        // Movement already caps that, the blend tree did not -- which had walking
        // diagonally blend 40% into the run clip. A longer stride and far more hip
        // travel than the ground actually being covered, and the camera takes all
        // of it straight off the head bone.
        Vector2 moveDir = Vector2.ClampMagnitude(movement.MoveInput, 1f);
        bool hasMoveInput = moveDir.magnitude > MoveThreshold;

        // Direction from the input, pace from the velocity. Intent is instant --
        // the moment a key goes down the model should start turning to face that
        // way -- but the speed it travels at is not, and at a standstill the
        // velocity has no direction to read anyway.
        Vector3 horizontalVelocity = movement.HorizontalVelocity;
        bool isMoving = horizontalVelocity.magnitude > SpeedThreshold;

        if (hasMoveInput)
        {
            float rawAngle = Mathf.Atan2(moveDir.x, moveDir.y) * Mathf.Rad2Deg;
            bool isBackward = moveDir.y < 0f;
            float referenceAngle = isBackward ? (rawAngle >= 0f ? 180f : -180f) : 0f;
            float targetFacingOffset = rawAngle - referenceAngle;

            // A sudden strafe reversal (D -> A or vice versa) can land the target
            // almost exactly 180 degrees from the current facing -- a near-tie for
            // "shortest path" that MoveTowardsAngle can resolve either direction
            // depending on tiny per-frame timing differences, occasionally spinning
            // the character through its back instead of the front. Bias near-ties
            // toward the front so it always resolves the same way.
            if (Mathf.Abs(Mathf.DeltaAngle(_facingOffset, targetFacingOffset)) > 175f)
                targetFacingOffset = Mathf.MoveTowardsAngle(targetFacingOffset, 0f, 5f);

            _facingOffset = Mathf.MoveTowardsAngle(_facingOffset, targetFacingOffset, turnSpeed * Time.deltaTime);
            _isRecoveringFromTurn = false;
        }
        else if (_isRecoveringFromTurn)
        {
            UpdateTurnRecovery();
        }
        else
        {
            _facingOffset -= look.YawDelta;

            // Nothing is pressed but the character is still coasting to a stop:
            // the legs are mid-stride and the animator is still in Locomotion, so
            // a turn fired now would cut the walk short over a stop the player
            // has not actually reached yet. The facing still holds its heading
            // against the camera above, it just doesn't turn to catch up.
            if (!isMoving)
            {
                if (_facingOffset <= -turnInPlaceToleranceAngle)
                {
                    _animator.SetTrigger(TurnRightHash);
                    StartTurnRecovery();
                }
                else if (_facingOffset >= turnInPlaceToleranceAngle)
                {
                    _animator.SetTrigger(TurnLeftHash);
                    StartTurnRecovery();
                }
            }
        }

        transform.localRotation = Quaternion.Euler(0f, _facingOffset, 0f);

        // Measured along the model's own forward, after it has been turned to face
        // wherever it is going. That axis is what the clips are: strafing right
        // has the model facing right and walking, so the projection is a full
        // forward walk, and going backwards has it still facing front with the
        // projection negative. Reading the sign off the root's axes instead put a
        // pure strafe at a forward component of nothing but float noise, flipping
        // the blend between a walk and a backwards walk from frame to frame.
        //
        // It also makes a reversal continuous, which is the other half of the
        // same point: pressing S at a full walk sweeps +1 -> 0 -> -1 as the
        // velocity actually turns around, rather than jumping straight to -1 over
        // a body still travelling forwards.
        float signedSpeed = isMoving ? Vector3.Dot(horizontalVelocity, transform.forward) : 0f;

        _animator.SetFloat(ForwardAmountHash, SpeedToForwardAmount(signedSpeed));
        _animator.SetBool(IsCrouchingHash, movement.IsCrouching);
        _animator.SetBool(IsMovingHash, isMoving);
    }

    // Metres per second along the model's facing onto the blend tree's own axis,
    // where 1 is a full walk stride, 2 a run, and the negatives their backwards
    // counterparts. The references come from movement rather than being constants
    // here so retuning a speed retunes the animation with it, and so that the
    // halves stay separate: a walk that tops out at 5 and a run that tops out at
    // 8 are not the same axis scaled, they are two clips with a seam at 1, and
    // stretching one figure across both would leave a full sprint reading as 1.6
    // with the run clip never fully reached.
    //
    // Backwards has its own pair of references, so which pair applies changes at
    // zero -- where both answer zero regardless, which is what keeps the sweep
    // through a reversal unbroken.
    private float SpeedToForwardAmount(float signedSpeed)
    {
        bool isBackward = signedSpeed < 0f;
        float speed = Mathf.Abs(signedSpeed);

        float walkSpeed = movement.GetGaitSpeed(false, isBackward);
        if (walkSpeed <= 0f)
            return 0f;

        float amount;
        if (speed <= walkSpeed)
        {
            amount = speed / walkSpeed;
        }
        else
        {
            // Crouching answers with its own walk speed for both gaits, so the
            // run half collapses and the blend holds at a full crouched stride.
            float runSpeed = movement.GetGaitSpeed(true, isBackward);
            amount = runSpeed > walkSpeed
                ? 1f + (speed - walkSpeed) / (runSpeed - walkSpeed)
                : 1f;
        }

        return isBackward ? -amount : amount;
    }

    // How long a fired turn trigger is given to actually reach the animator: the
    // time turning back unaided would have taken anyway. Past that, waiting for
    // the animation has cost more than not having it. Measured against turnSpeed
    // -- the rate the model already turns at while moving -- so a failure mode
    // doesn't get a tuning value of its own.
    private float TurnStartGracePeriod => turnSpeed > 0f
        ? Mathf.Abs(_turnStartOffset) / turnSpeed
        : 0f;

    private void StartTurnRecovery()
    {
        _isRecoveringFromTurn = true;
        _hasEnteredTurnState = false;
        _turnStartWaitTimer = 0f;
        _turnStartOffset = _facingOffset;
    }

    // The facing offset is driven straight off the turn animation's own progress
    // rather than by a rate of its own, so it reaches neutral exactly as the clip
    // ends. Nothing has to be assigned or kept in sync: swap the clip, retime it,
    // change the transition, scale the animator's speed -- the body follows,
    // because the animator is the thing being read.
    private void UpdateTurnRecovery()
    {
        bool isTurning = TryGetTurnProgress(out float progress);

        if (isTurning)
        {
            _hasEnteredTurnState = true;
            _facingOffset = Mathf.LerpAngle(_turnStartOffset, 0f, progress);

            if (progress < 1f)
                return;
        }
        else if (!_hasEnteredTurnState)
        {
            // The trigger was set and the animator hasn't acted on it yet. Hold
            // the offset rather than start straightening, so the body doesn't move
            // before the animation that moves it. But a trigger can also be
            // swallowed -- consumed by another transition, or a state renamed in
            // the controller -- and that must not leave the character stuck at
            // ninety degrees. Past the grace period it gives up and turns back
            // unaided at turnSpeed, which is smoother than snapping and is the
            // very thing the grace period was measured against.
            _turnStartWaitTimer += Time.deltaTime;
            if (_turnStartWaitTimer < TurnStartGracePeriod)
                return;

            _facingOffset = Mathf.MoveTowardsAngle(_facingOffset, 0f, turnSpeed * Time.deltaTime);
            if (Mathf.Abs(_facingOffset) > 0.01f)
                return;
        }

        // The clip played out, or was left early, or never arrived and the body
        // has finished squaring up without it.
        _facingOffset = 0f;
        _isRecoveringFromTurn = false;
    }

    private bool TryGetTurnProgress(out float progress)
    {
        AnimatorStateInfo info = GetEffectiveStateInfo(BaseLayerIndex);

        foreach (string stateName in TurnStateNames)
        {
            if (!info.IsName(stateName))
                continue;

            progress = Mathf.Clamp01(info.normalizedTime);
            return true;
        }

        progress = 0f;
        return false;
    }

    // Two clips serve four moves. ClimbingEnter is the hold at the bottom of the
    // rail -- forward to take it, backwards to let go. ClimbingExit is the move
    // over the top edge -- forward to climb off, backwards to climb on. Which
    // way round is set by the shared speed parameter; the transitions that start
    // a reversed one enter at offset 1 so the clip has somewhere to run back from.
    public void PlayLadderMountFromBottom()
    {
        _animator.SetFloat(LadderTransitionSpeedHash, 1f);
        _animator.SetTrigger(LadderEnterHash);
    }

    public void PlayLadderMountFromTop()
    {
        _animator.SetFloat(LadderTransitionSpeedHash, -1f);
        _animator.SetTrigger(LadderEnterFromTopHash);
    }

    public void PlayLadderDismountAtTop()
    {
        _animator.SetFloat(LadderTransitionSpeedHash, 1f);
        _animator.SetTrigger(LadderFinishHash);
    }

    public void PlayLadderDismountAtBottom()
    {
        _animator.SetFloat(LadderTransitionSpeedHash, -1f);
        _animator.SetTrigger(LadderExitBottomHash);
    }

    // Both mounts leave for the climb on the same signal -- the state they leave
    // from differs, the meaning doesn't.
    public void PlayLadderMountComplete() => _animator.SetTrigger(LadderMountCompleteHash);

    // -1, not 0, when the animator isn't in the state at all. A caller waiting on
    // one of these has to be able to tell "hasn't started yet" from "just
    // started" -- otherwise a trigger that never lands looks exactly like an
    // animation frozen on its first frame, and the wait never ends.
    public float LadderMountProgress => GetLadderCarProgress(LadderMountStateName);
    public float LadderDismountProgress => GetLadderCarProgress(LadderDismountStateName);

    private float GetLadderCarProgress(string stateName)
    {
        AnimatorStateInfo info = GetLadderCarStateInfo(stateName, out bool isInState);
        return isInState ? Mathf.Clamp01(info.normalizedTime) : -1f;
    }

    public void PlayCarEnter()
    {
        _animator.SetFloat(CarTransitionSpeedHash, 1f);
        _animator.SetTrigger(CarEnterHash);
    }

    public void PlayCarExit()
    {
        _animator.SetFloat(CarTransitionSpeedHash, -1f);
        _animator.SetTrigger(CarExitHash);
    }

    public void PlayCarEnterComplete() => _animator.SetTrigger(CarEnterCompleteHash);

    // Lets an equipped item (e.g. Weapon) override the spine/chest/upperChest/neck
    // aim rig weights (independently per bone) while it's active, without
    // PlayerAnimator needing to know anything about items -- same push-values-in
    // pattern as hand IK targets.
    public void SetAimRigWeightOverride(float spineWeight, float chestWeight, float upperChestWeight, float neckWeight)
    {
        _hasAimRigWeightOverride = true;
        _spineAimWeightOverride = spineWeight;
        _chestAimWeightOverride = chestWeight;
        _upperChestAimWeightOverride = upperChestWeight;
        _neckAimWeightOverride = neckWeight;
    }

    public void ClearAimRigWeightOverride() => _hasAimRigWeightOverride = false;

    // Lets an item keep the character in the item pose after it has already been
    // unequipped -- for the length of its own put-away animation, so the body
    // stays on the weapon until it's actually away rather than switching on the
    // command. Same push-values-in pattern as hand IK targets and aim rig
    // weights; an item that never calls this simply switches immediately.
    //
    // Applied straight away instead of only flagging it for the next Update.
    // The item ends this hold from a coroutine, and coroutines resume AFTER
    // every Update but BEFORE the Animator evaluates -- so merely setting the
    // flag would leave this frame's already-written weight of 1 standing while
    // the item has released its hand IK and put the weapon away, showing one
    // frame of the character holding an empty item pose.
    public void SetItemPoseHeld(bool isHeld)
    {
        _isItemPoseHeld = isHeld;
        ApplyItemPose();
    }

    private void ApplyItemPose()
    {
        if (_animator == null)
            return;

        // IsChangingItem is what carries the pose across a swap, and it matters more
        // than a single frame usually would. The Item Layer's two transitions are
        // uninterruptible, so one frame of this going false starts a 0.1s blend into
        // ItemIdle that setting it back true cannot cancel -- 0.2s of the character
        // dropping the pose and picking it up again, out of one frame's gap.
        //
        // That gap is real: the hold an item sets on its way out ends the moment its
        // holster clip does, which on a swap lands a frame before the next item is
        // drawn. Nothing is lost bridging it -- a swap is one continuous request and
        // the hands are occupied for every frame of it. A stow with nothing queued
        // behind it still lets go, which is the case the hold was written for.
        bool isAiming = _isItemPoseHeld
            || (items != null && (items.HasEquippedItem || items.IsChangingItem));

        // Drives the Item Layer's own ItemIdle <-> ItemLayer transitions, so it
        // has to be held alongside the weight below rather than dropped early --
        // at weight 1 an early drop would visibly snap the body into ItemIdle.
        _animator.SetBool(IsAimingHash, isAiming);

        // Snapped instantly (not lerped) once the hold ends -- by then the
        // weapon is away and the layer has nothing left to show.
        if (_itemLayerIndex >= 0)
            _animator.SetLayerWeight(_itemLayerIndex, isAiming ? 1f : 0f);
    }

    public void SetLeftHandIKTarget(Transform target, Transform hint = null, float? transitionDuration = null)
    {
        _leftHandIKTarget = target;
        _leftHandIKHint = hint;
        _leftHandIKTransitionDurationOverride = transitionDuration;
    }

    public void SetRightHandIKTarget(Transform target, Transform hint = null, float? transitionDuration = null)
    {
        _rightHandIKTarget = target;
        _rightHandIKHint = hint;
        _rightHandIKTransitionDurationOverride = transitionDuration;
    }

    public void ClearHandIKTargets()
    {
        // Refused while another item is on its way up. The hands are about to be
        // handed straight to it, and letting go in between costs twice: the IK is
        // weighted off for the frame the outgoing item releases in, dropping the
        // hands to the raw pose, and clearing the target resets the blend so they
        // then sweep back onto the new weapon over a full transition instead of
        // simply following it.
        //
        // Left in place they stay on the outgoing item's grips for that one frame,
        // which by then are down at the hip, and the handover reads as the hands
        // going with one weapon and coming back up with the next.
        if (items != null && items.IsChangingItem)
            return;

        _leftHandIKTarget = null;
        _rightHandIKTarget = null;
        _leftHandIKHint = null;
        _rightHandIKHint = null;
    }

    public float CarTransitionProgress
    {
        get
        {
            AnimatorStateInfo entryInfo = GetLadderCarStateInfo("CarEntry", out bool isInEntry);
            if (isInEntry)
                return Mathf.Clamp01(entryInfo.normalizedTime);

            // CarExit plays its own dedicated clip forward, but the caller (UpdateCarTransition)
            // interpolates DoorLeft->FrontLeft the same way for both directions, so exit progress
            // is reported as the complement -- 1 (still at FrontLeft) down to 0 (back at DoorLeft).
            AnimatorStateInfo exitInfo = GetLadderCarStateInfo("CarExit", out bool isInExit);
            if (isInExit)
                return 1f - Mathf.Clamp01(exitInfo.normalizedTime);

            return _animator.GetFloat(CarTransitionSpeedHash) < 0f ? 1f : 0f;
        }
    }

    private void OnAnimatorMove()
    {
        GetLadderCarStateInfo("LadderClimbFinish", out bool isInLadderFinish);

        if (isInLadderFinish && movement != null)
            movement.ApplyTransitionMotion(_animator.deltaPosition);
    }

    // LadderClimbFinish/CarEntry/CarExit live on the LadderCar layer, the turn
    // states on Base Layer -- a state is only ever found on its own layer, so the
    // caller has to say which one it means.
    private AnimatorStateInfo GetLadderCarStateInfo(string stateName, out bool isInState)
        => GetStateInfo(stateName, _ladderCarLayerIndex >= 0 ? _ladderCarLayerIndex : 0, out isInState);

    private AnimatorStateInfo GetStateInfo(string stateName, int layer, out bool isInState)
    {
        AnimatorStateInfo info = GetEffectiveStateInfo(layer);
        isInState = info.IsName(stateName);
        return info;
    }

    // Mid-transition the state that matters is the one being entered, not the one
    // being left -- otherwise a state isn't seen as reached until its blend has
    // finished, and anything waiting on it starts a transition's worth of time late.
    private AnimatorStateInfo GetEffectiveStateInfo(int layer)
        => _animator.IsInTransition(layer)
            ? _animator.GetNextAnimatorStateInfo(layer)
            : _animator.GetCurrentAnimatorStateInfo(layer);

    // Only touches the constraints while an item is pushing an override (or while
    // still lerping back from one that just ended), and restores whatever they
    // were set to right before that override started (not a value cached once at
    // Awake) -- otherwise leaves their weight alone entirely once settled, so it
    // stays freely tweakable on the constraint's own Inspector slider (including
    // live in Play Mode) whenever no item is overriding it. Both the apply and
    // restore are lerped (not snapped) so entering/leaving an override -- e.g. a
    // take/holster animation finishing -- doesn't pop the pose in a single frame.
    private const float AimRigWeightSettleThreshold = 0.001f;

    private bool _isRestoringAimRigWeight;

    private void UpdateAimRigWeightOverride()
    {
        // Held exactly where the outgoing item left them for the length of a swap.
        // An item clears its override the instant it is unequipped, so between one
        // going away and the next coming up nobody is pushing weights and the
        // constraints unwind toward their resting values -- measured at 0.50 mid-
        // swap against the 0.85 the pistol asks for. The torso untwists and twists
        // back over the better part of a second, which is the whole of what is left
        // of the swap glitch now that the layer weight and the hand IK hold.
        //
        // Returning outright rather than skipping the restore, so the bookkeeping
        // below is untouched too: the pre-override weights stay the ones from before
        // the first item, and the next item's push lerps on from here instead of
        // caching a value that is itself an override.
        if (!_hasAimRigWeightOverride && items != null && items.IsChangingItem)
            return;

        float t = layerWeightTransitionSpeed * Time.deltaTime;

        if (_hasAimRigWeightOverride)
        {
            if (!_wasAimRigWeightOverrideActive)
            {
                if (spineAim != null) _spineAimPreOverrideWeight = spineAim.weight;
                if (chestAim != null) _chestAimPreOverrideWeight = chestAim.weight;
                if (upperChestAim != null) _upperChestAimPreOverrideWeight = upperChestAim.weight;
                if (neckAim != null) _neckAimPreOverrideWeight = neckAim.weight;
            }

            if (spineAim != null) spineAim.weight = Mathf.Lerp(spineAim.weight, _spineAimWeightOverride, t);
            if (chestAim != null) chestAim.weight = Mathf.Lerp(chestAim.weight, _chestAimWeightOverride, t);
            if (upperChestAim != null) upperChestAim.weight = Mathf.Lerp(upperChestAim.weight, _upperChestAimWeightOverride, t);
            if (neckAim != null) neckAim.weight = Mathf.Lerp(neckAim.weight, _neckAimWeightOverride, t);

            _isRestoringAimRigWeight = false;
        }
        else if (_wasAimRigWeightOverrideActive || _isRestoringAimRigWeight)
        {
            bool stillRestoring = false;

            if (spineAim != null)
            {
                spineAim.weight = Mathf.Lerp(spineAim.weight, _spineAimPreOverrideWeight, t);
                stillRestoring |= Mathf.Abs(spineAim.weight - _spineAimPreOverrideWeight) > AimRigWeightSettleThreshold;
            }
            if (chestAim != null)
            {
                chestAim.weight = Mathf.Lerp(chestAim.weight, _chestAimPreOverrideWeight, t);
                stillRestoring |= Mathf.Abs(chestAim.weight - _chestAimPreOverrideWeight) > AimRigWeightSettleThreshold;
            }
            if (upperChestAim != null)
            {
                upperChestAim.weight = Mathf.Lerp(upperChestAim.weight, _upperChestAimPreOverrideWeight, t);
                stillRestoring |= Mathf.Abs(upperChestAim.weight - _upperChestAimPreOverrideWeight) > AimRigWeightSettleThreshold;
            }
            if (neckAim != null)
            {
                neckAim.weight = Mathf.Lerp(neckAim.weight, _neckAimPreOverrideWeight, t);
                stillRestoring |= Mathf.Abs(neckAim.weight - _neckAimPreOverrideWeight) > AimRigWeightSettleThreshold;
            }

            _isRestoringAimRigWeight = stillRestoring;
        }

        _wasAimRigWeightOverrideActive = _hasAimRigWeightOverride;
    }


    // In LateUpdate because it is the only place a plain bone write survives: the
    // aim constraints re-pose this same chain during the rig's evaluation and would
    // overwrite anything set earlier. Re-applied every frame, since the animation
    // wipes it back to the clip's pose each time -- which is what keeps it from
    // accumulating.
    private void LateUpdate()
    {
        ApplyPeekSlide();
    }

    private void ApplyPeekSlide()
    {
        if (look == null)
            return;

        // Gated on the lean itself rather than on the slide, so a setup that rolls
        // without sliding -- or the other way round -- still gets its half.
        if (Mathf.Approximately(look.PeekAmount, 0f))
            return;

        // A third each, applied in order up the chain. Moving a bone takes its
        // children with it, so the chest's own share lands on top of the spine's
        // and the upper chest's on top of both -- three equal steps that add up to
        // the whole slide by the time they reach the shoulders.
        Vector3 step = transform.root.right * (look.PeekOffset / 3f);

        if (peekSpineBone != null)
            peekSpineBone.position += step;

        if (peekChestBone != null)
            peekChestBone.position += step;

        if (peekUpperChestBone != null)
            peekUpperChestBone.position += step;

        // About the body's forward, the same axis and the same angle the pivot
        // rolled by, so the head and the view cock together. Applied last: it hangs
        // off the chain above, and rolling it before those had moved would only
        // have it carried again.
        if (peekHeadBone != null)
        {
            Quaternion tilt = Quaternion.AngleAxis(
                look.PeekTiltAngle * peekHeadTiltMultiplier, transform.root.forward);
            peekHeadBone.rotation = tilt * peekHeadBone.rotation;
        }
    }

    private void OnAnimatorIK(int layerIndex)
    {
        // Car/ladder grip IK (steering wheel, gear, handbrake, door) now needs to
        // apply on the LadderCar layer too -- IK is set per-layer, so a layer
        // that never calls the Set* methods during its own OnAnimatorIK gets no
        // IK influence even if another layer already set the same goal this frame.
        if (layerIndex != 0 && layerIndex != _ladderCarLayerIndex)
            return;

        ApplyBothHandIK();
    }

    // How far the arm is about to be carried sideways, after this solve has already
    // finished. The slide lands in LateUpdate -- it has to, or the aim constraints
    // overwrite it -- but the hands are solved back here, against shoulders that
    // have not moved yet while the weapon already has. Left alone the slide is
    // applied twice over: once to the weapon, once again to the arm chasing it.
    //
    // Taken off the goal instead, the hand is aimed short by exactly what the
    // torso is about to add, and lands on the weapon. Exact rather than
    // approximate, because a translation is trivially invertible: the upper chest
    // ends at three equal thirds, which is the whole offset, and everything below
    // the shoulder rides it rigidly.
    private Vector3 PeekSlideCompensation =>
        look != null ? transform.root.right * look.PeekOffset : Vector3.zero;

    private void ApplyBothHandIK()
    {
        ApplyHandIK(AvatarIKGoal.LeftHand, AvatarIKHint.LeftElbow, HumanBodyBones.LeftHand, _leftHandIKTarget, _leftHandIKHint, _leftHandIKState, _leftHandIKTransitionDurationOverride);
        ApplyHandIK(AvatarIKGoal.RightHand, AvatarIKHint.RightElbow, HumanBodyBones.RightHand, _rightHandIKTarget, _rightHandIKHint, _rightHandIKState, _rightHandIKTransitionDurationOverride);
    }

    private void ApplyHandIK(AvatarIKGoal goal, AvatarIKHint hint, HumanBodyBones handBone, Transform target, Transform hintTarget, HandIKState state, float? transitionDurationOverride)
    {
        float weight = target != null ? 1f : 0f;
        _animator.SetIKPositionWeight(goal, weight);
        _animator.SetIKRotationWeight(goal, weight);

        // Without an explicit hint, Unity guesses the elbow's bend direction from
        // the current pose each frame. That guess isn't stable when the shoulder
        // it's guessing from keeps moving (locomotion's own hip/spine sway), so the
        // forearm can visibly swing frame to frame even while the hand itself sits
        // exactly on target -- an explicit hint removes the ambiguity entirely.
        Vector3 peekSlideCompensation = PeekSlideCompensation;

        _animator.SetIKHintPositionWeight(hint, hintTarget != null ? 1f : 0f);
        if (hintTarget != null)
            _animator.SetIKHintPosition(hint, hintTarget.position - peekSlideCompensation);

        if (target == null)
        {
            state.PreviousTarget = null;
            return;
        }

        if (target != state.PreviousTarget)
        {
            if (state.PreviousTarget == null)
            {
                Transform bone = _animator.GetBoneTransform(handBone);
                state.CurrentPosition = bone.position;
                state.CurrentRotation = bone.rotation;
            }

            // The car's actual rigidbody-driven root, not just target's immediate parent -- grips
            // can sit a level or two under intermediate (sometimes separately-animated, e.g.
            // KeyIK) objects, and re-projecting the blend start against one of those instead of
            // the car's real moving frame is exactly what would make the start point lag behind
            // at speed and the hand look like it beelines somewhere wrong before correcting.
            state.BlendReference = target.root;
            if (state.BlendReference != null)
            {
                state.BlendStartLocalPosition = state.BlendReference.InverseTransformPoint(state.CurrentPosition);
                state.BlendStartLocalRotation = Quaternion.Inverse(state.BlendReference.rotation) * state.CurrentRotation;
            }
            else
            {
                state.BlendStartLocalPosition = state.CurrentPosition;
                state.BlendStartLocalRotation = state.CurrentRotation;
            }

            state.TransitionDuration = transitionDurationOverride ?? handIKTransitionDuration;
            state.BlendT = 0f;
            state.PreviousTarget = target;
        }
        else if (state.BlendT < 1f)
        {
            state.BlendT = Mathf.Min(1f, state.BlendT + Time.unscaledDeltaTime / state.TransitionDuration);
        }

        if (state.BlendT >= 1f)
        {
            state.CurrentPosition = target.position;
            state.CurrentRotation = target.rotation;
        }
        else
        {
            Vector3 blendStartPosition = state.BlendReference != null
                ? state.BlendReference.TransformPoint(state.BlendStartLocalPosition)
                : state.BlendStartLocalPosition;
            Quaternion blendStartRotation = state.BlendReference != null
                ? state.BlendReference.rotation * state.BlendStartLocalRotation
                : state.BlendStartLocalRotation;

            state.CurrentPosition = Vector3.Lerp(blendStartPosition, target.position, state.BlendT);
            state.CurrentRotation = Quaternion.Slerp(blendStartRotation, target.rotation, state.BlendT);
        }

        // Rotation is left alone: the slide is a translation, and a translation
        // doesn't turn the hand.
        _animator.SetIKPosition(goal, state.CurrentPosition - peekSlideCompensation);
        _animator.SetIKRotation(goal, state.CurrentRotation);
    }
}

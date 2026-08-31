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
    [SerializeField] private float forwardAmountDampTime = 0.15f;
    [SerializeField] private float turnInPlaceToleranceAngle = 90f;


    [Header("Aim Rig")]
    [SerializeField] private MultiAimConstraint spineAim;
    [SerializeField] private MultiAimConstraint chestAim;
    [SerializeField] private MultiAimConstraint upperChestAim;
    [SerializeField] private MultiAimConstraint neckAim;

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

    // The mount over the top edge has no clip of its own: it is the dismount
    // played backwards, which is why both live in the same state. PlayerMovement
    // is what inverts the progress for that case -- the reversal is content, and
    // stays in one place rather than spreading through the flow.
    private const string LadderMountStateName = "LadderClimbEnter";
    private const string LadderDismountStateName = "LadderClimbFinish";

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
            float entryForwardAmount = movement.IsSlidingToEntry ? 1f : 0f;

            _animator.SetFloat(ForwardAmountHash, entryForwardAmount, forwardAmountDampTime, Time.deltaTime);
            _animator.SetBool(IsMovingHash, entryForwardAmount > 0.05f);
            _animator.SetBool(IsCrouchingHash, false);
            return;
        }

        _isSquaringUpForEntry = false;

        Vector2 moveDir = movement.MoveInput;
        float speed = moveDir.magnitude;
        bool isBackward = moveDir.y < 0f;
        float forwardAmount = 0f;

        if (speed > 0.05f)
        {
            bool useRunAnimation = movement.IsSprinting || !movement.IsGroundedStable;
            float sprintMultiplier = useRunAnimation ? 2f : 1f;
            forwardAmount = (isBackward ? -speed : speed) * sprintMultiplier;

            float rawAngle = Mathf.Atan2(moveDir.x, moveDir.y) * Mathf.Rad2Deg;
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

        transform.localRotation = Quaternion.Euler(0f, _facingOffset, 0f);

        _animator.SetFloat(ForwardAmountHash, forwardAmount, forwardAmountDampTime, Time.deltaTime);
        _animator.SetBool(IsCrouchingHash, movement.IsCrouching);
        _animator.SetBool(IsMovingHash, speed > 0.05f);
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

    // Lets an equipped item (e.g. Pistol) override the spine/chest/upperChest/neck
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

        bool isAiming = (items != null && items.HasEquippedItem) || _isItemPoseHeld;

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
        _animator.SetIKHintPositionWeight(hint, hintTarget != null ? 1f : 0f);
        if (hintTarget != null)
            _animator.SetIKHintPosition(hint, hintTarget.position);

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

        _animator.SetIKPosition(goal, state.CurrentPosition);
        _animator.SetIKRotation(goal, state.CurrentRotation);
    }
}

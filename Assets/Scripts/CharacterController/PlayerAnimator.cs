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
    [SerializeField] private float turnAnimResetSpeed = 400f;
    [SerializeField] private LayerMask groundLayer = ~0;
    [SerializeField] private float footIKRayDistance = 0.5f;
    [SerializeField] private float footIKGroundOffset = 0.05f;
    [SerializeField] private float footIKMaxOffset = 0.15f;
    [SerializeField] private float footIKHeightSmoothSpeed = 10f;
    [SerializeField] private float pelvisAdjustSpeed = 8f;

    [Header("Aim Rig")]
    [SerializeField] private MultiAimConstraint spineAim;
    [SerializeField] private MultiAimConstraint chestAim;
    [SerializeField] private MultiAimConstraint upperChestAim;
    [SerializeField] private MultiAimConstraint neckAim;

    [Header("Peek")]
    [SerializeField] private float peekMaxOffset = 0.2f;
    [SerializeField] private float peekBendSpeed = 8f;

    [Header("Car Turn Lag")]
    [SerializeField] private float carTurnLagSpeed = 6f;
    [SerializeField] private float carTurnLagMaxAngle = 30f;

    [Header("Hand IK")]
    [SerializeField] private float handIKTransitionDuration = 0.08f;

    [Header("Layer Weights")]
    [SerializeField] private float layerWeightTransitionSpeed = 8f;

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
    private static readonly int LadderEnterFromTopCompleteHash = Animator.StringToHash("LadderEnterFromTopComplete");
    private static readonly int IsInCarHash = Animator.StringToHash("IsInCar");
    private static readonly int CarTransitionSpeedHash = Animator.StringToHash("CarTransitionSpeed");
    private static readonly int CarEnterHash = Animator.StringToHash("CarEnter");
    private static readonly int CarExitHash = Animator.StringToHash("CarExit");
    private static readonly int CarEnterCompleteHash = Animator.StringToHash("CarEnterComplete");
    private static readonly int IsAimingHash = Animator.StringToHash("IsAiming");

    private Animator _animator;
    private int _pistolAimLayerIndex;
    private int _ladderCarLayerIndex;
    private float _pistolAimLayerWeight;
    private float _ladderCarLayerWeight;
    private float _facingOffset;
    private bool _isRecoveringFromTurn;
    private float _leftFootHeight;
    private float _rightFootHeight;
    private float _currentPelvisOffset;
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
    private float _currentPeekOffset;
    private Quaternion _smoothedCarRotation;
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
        _pistolAimLayerIndex = _animator.GetLayerIndex("PistolAim");
        _ladderCarLayerIndex = _animator.GetLayerIndex("LadderCar");

        _leftFootHeight = _animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y;
        _rightFootHeight = _animator.GetBoneTransform(HumanBodyBones.RightFoot).position.y;

        _smoothedCarRotation = movement.transform.rotation;
    }

    private void Update()
    {
        _animator.SetBool(IsClimbingLadderHash, movement.IsClimbingLadder);
        _animator.SetFloat(ClimbSpeedHash, movement.IsClimbingLadder ? movement.MoveInput.y : 0f);
        _animator.SetBool(IsInCarHash, movement.IsInCar);

        bool isAiming = items != null && items.HasEquippedItem;
        _animator.SetBool(IsAimingHash, isAiming);

        // Lerped instead of snapped straight to 0/1 -- an instant layer weight
        // jump pops the masked bones (arms/whole body) directly to the other
        // layer's pose in a single frame, which reads as a hard, sudden jerk in
        // the first-person view (most visibly the weapon/arms, right in frame).
        if (_pistolAimLayerIndex >= 0)
        {
            _pistolAimLayerWeight = Mathf.Lerp(_pistolAimLayerWeight, isAiming ? 1f : 0f, layerWeightTransitionSpeed * Time.deltaTime);
            _animator.SetLayerWeight(_pistolAimLayerIndex, _pistolAimLayerWeight);
        }

        if (_ladderCarLayerIndex >= 0)
        {
            float targetLadderCarWeight = (movement.IsClimbingLadder || movement.IsInCar) ? 1f : 0f;
            _ladderCarLayerWeight = Mathf.Lerp(_ladderCarLayerWeight, targetLadderCarWeight, layerWeightTransitionSpeed * Time.deltaTime);
            _animator.SetLayerWeight(_ladderCarLayerIndex, _ladderCarLayerWeight);
        }

        UpdateAimRigWeightOverride();

        if (movement.IsClimbingLadder || movement.IsInCar)
        {
            _facingOffset = Mathf.MoveTowardsAngle(_facingOffset, 0f, turnAnimResetSpeed * Time.deltaTime);
            transform.localRotation = Quaternion.Euler(0f, _facingOffset, 0f);
            return;
        }

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
            _facingOffset = Mathf.MoveTowardsAngle(_facingOffset, 0f, turnAnimResetSpeed * Time.deltaTime);
            if (Mathf.Abs(_facingOffset) < 0.5f)
            {
                _facingOffset = 0f;
                _isRecoveringFromTurn = false;
            }
        }
        else
        {
            _facingOffset -= look.YawDelta;

            if (_facingOffset <= -turnInPlaceToleranceAngle)
            {
                _animator.SetTrigger(TurnRightHash);
                _isRecoveringFromTurn = true;
            }
            else if (_facingOffset >= turnInPlaceToleranceAngle)
            {
                _animator.SetTrigger(TurnLeftHash);
                _isRecoveringFromTurn = true;
            }
        }

        transform.localRotation = Quaternion.Euler(0f, _facingOffset, 0f);

        _animator.SetFloat(ForwardAmountHash, forwardAmount, forwardAmountDampTime, Time.deltaTime);
        _animator.SetBool(IsCrouchingHash, movement.IsCrouching);
        _animator.SetBool(IsMovingHash, speed > 0.05f);
    }

    private void LateUpdate()
    {
        ApplyPeek();
        ApplyCarTurnLag();
    }

    private void ApplyCarTurnLag()
    {
        if (!movement.IsInCar)
        {
            _smoothedCarRotation = movement.transform.rotation;
            return;
        }

        _smoothedCarRotation = Quaternion.Slerp(_smoothedCarRotation, movement.transform.rotation, carTurnLagSpeed * Time.deltaTime);

        Quaternion twistDelta = Quaternion.Inverse(movement.transform.rotation) * _smoothedCarRotation;

        twistDelta.ToAngleAxis(out float twistAngle, out Vector3 twistAxis);
        if (twistAngle > 180f)
            twistAngle -= 360f;
        twistAngle = Mathf.Clamp(twistAngle, -carTurnLagMaxAngle, carTurnLagMaxAngle);
        twistDelta = Quaternion.AngleAxis(twistAngle, twistAxis);

        Transform neckBone = _animator.GetBoneTransform(HumanBodyBones.Neck);
        if (neckBone != null)
            neckBone.localRotation *= twistDelta;
    }

    public void PlayLadderEnter() => _animator.SetTrigger(LadderEnterHash);

    public void PlayLadderFinish()
    {
        _animator.SetFloat(LadderTransitionSpeedHash, 1f);
        _animator.SetTrigger(LadderFinishHash);
    }

    public void PlayLadderEnterFromTop()
    {
        _animator.SetFloat(LadderTransitionSpeedHash, -1f);
        _animator.SetTrigger(LadderEnterFromTopHash);
    }

    public void PlayLadderEnterFromTopComplete() => _animator.SetTrigger(LadderEnterFromTopCompleteHash);

    public float LadderTransitionProgress
    {
        get
        {
            AnimatorStateInfo info = GetStateInfo("LadderClimbFinish", out bool isInState);
            return isInState ? Mathf.Clamp01(info.normalizedTime) : 0f;
        }
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
            AnimatorStateInfo entryInfo = GetStateInfo("CarEntry", out bool isInEntry);
            if (isInEntry)
                return Mathf.Clamp01(entryInfo.normalizedTime);

            // CarExit plays its own dedicated clip forward, but the caller (UpdateCarTransition)
            // interpolates DoorLeft->FrontLeft the same way for both directions, so exit progress
            // is reported as the complement -- 1 (still at FrontLeft) down to 0 (back at DoorLeft).
            AnimatorStateInfo exitInfo = GetStateInfo("CarExit", out bool isInExit);
            if (isInExit)
                return 1f - Mathf.Clamp01(exitInfo.normalizedTime);

            return _animator.GetFloat(CarTransitionSpeedHash) < 0f ? 1f : 0f;
        }
    }

    private void OnAnimatorMove()
    {
        GetStateInfo("LadderClimbFinish", out bool isInLadderFinish);

        if (isInLadderFinish && movement != null)
            movement.ApplyTransitionMotion(_animator.deltaPosition);
    }

    private AnimatorStateInfo GetStateInfo(string stateName, out bool isInState)
    {
        // LadderClimbFinish/CarEntry/CarExit all live on the LadderCar layer, not
        // Base Layer (index 0) -- querying layer 0 here would never find them.
        int layer = _ladderCarLayerIndex >= 0 ? _ladderCarLayerIndex : 0;

        if (_animator.IsInTransition(layer))
        {
            AnimatorStateInfo nextInfo = _animator.GetNextAnimatorStateInfo(layer);
            isInState = nextInfo.IsName(stateName);
            return nextInfo;
        }

        AnimatorStateInfo currentInfo = _animator.GetCurrentAnimatorStateInfo(layer);
        isInState = currentInfo.IsName(stateName);
        return currentInfo;
    }

    // Only touches the constraints while an item is pushing an override, and restores
    // whatever they were set to right before that override started (not a value
    // cached once at Awake) the instant it ends -- otherwise leaves their weight
    // alone entirely, so it stays freely tweakable on the constraint's own Inspector
    // slider (including live in Play Mode) whenever no item is overriding it.
    private void UpdateAimRigWeightOverride()
    {
        if (_hasAimRigWeightOverride)
        {
            if (!_wasAimRigWeightOverrideActive)
            {
                if (spineAim != null) _spineAimPreOverrideWeight = spineAim.weight;
                if (chestAim != null) _chestAimPreOverrideWeight = chestAim.weight;
                if (upperChestAim != null) _upperChestAimPreOverrideWeight = upperChestAim.weight;
                if (neckAim != null) _neckAimPreOverrideWeight = neckAim.weight;
            }

            if (spineAim != null) spineAim.weight = _spineAimWeightOverride;
            if (chestAim != null) chestAim.weight = _chestAimWeightOverride;
            if (upperChestAim != null) upperChestAim.weight = _upperChestAimWeightOverride;
            if (neckAim != null) neckAim.weight = _neckAimWeightOverride;
        }
        else if (_wasAimRigWeightOverrideActive)
        {
            if (spineAim != null) spineAim.weight = _spineAimPreOverrideWeight;
            if (chestAim != null) chestAim.weight = _chestAimPreOverrideWeight;
            if (upperChestAim != null) upperChestAim.weight = _upperChestAimPreOverrideWeight;
            if (neckAim != null) neckAim.weight = _neckAimPreOverrideWeight;
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

        // Foot/pelvis IK only makes sense while planted -- mid-stride the swing
        // foot is naturally lifted well off the ground as part of ordinary
        // walking/running, and a raycast from it can still reach the real floor
        // below, which this system would otherwise mistake for uneven ground and
        // pull the pelvis down on every step.
        bool isStationary = movement.MoveInput.sqrMagnitude < 0.01f;

        if (movement.IsClimbingLadder || movement.IsInCar || !isStationary)
        {
            _animator.SetIKPositionWeight(AvatarIKGoal.LeftFoot, 0f);
            _animator.SetIKRotationWeight(AvatarIKGoal.LeftFoot, 0f);
            _animator.SetIKPositionWeight(AvatarIKGoal.RightFoot, 0f);
            _animator.SetIKRotationWeight(AvatarIKGoal.RightFoot, 0f);
            _currentPelvisOffset = Mathf.Lerp(_currentPelvisOffset, 0f, pelvisAdjustSpeed * Time.deltaTime);
            _animator.bodyPosition += Vector3.up * _currentPelvisOffset;
            ApplyBothHandIK();
            return;
        }

        Transform leftFootBone = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        Transform rightFootBone = _animator.GetBoneTransform(HumanBodyBones.RightFoot);

        bool leftHit = Physics.Raycast(leftFootBone.position + Vector3.up * footIKRayDistance, Vector3.down, out RaycastHit leftHitInfo, footIKRayDistance * 2f, groundLayer);
        bool rightHit = Physics.Raycast(rightFootBone.position + Vector3.up * footIKRayDistance, Vector3.down, out RaycastHit rightHitInfo, footIKRayDistance * 2f, groundLayer);

        float leftOffset = leftHit ? (leftHitInfo.point.y + footIKGroundOffset) - leftFootBone.position.y : 0f;
        float rightOffset = rightHit ? (rightHitInfo.point.y + footIKGroundOffset) - rightFootBone.position.y : 0f;

        float leftWeight = leftHit ? Mathf.Clamp01(1f - Mathf.Abs(leftOffset) / footIKMaxOffset) : 0f;
        float rightWeight = rightHit ? Mathf.Clamp01(1f - Mathf.Abs(rightOffset) / footIKMaxOffset) : 0f;

        float targetPelvisOffset = 0f;
        if (leftWeight > 0f)
            targetPelvisOffset = Mathf.Min(targetPelvisOffset, leftOffset * leftWeight);
        if (rightWeight > 0f)
            targetPelvisOffset = Mathf.Min(targetPelvisOffset, rightOffset * rightWeight);

        _currentPelvisOffset = Mathf.Lerp(_currentPelvisOffset, targetPelvisOffset, pelvisAdjustSpeed * Time.deltaTime);
        _animator.bodyPosition += Vector3.up * _currentPelvisOffset;

        ApplyFootIK(AvatarIKGoal.LeftFoot, leftHitInfo, leftWeight, ref _leftFootHeight);
        ApplyFootIK(AvatarIKGoal.RightFoot, rightHitInfo, rightWeight, ref _rightFootHeight);

        // Hand IK reads its target transforms' CURRENT positions -- calling this
        // after the pelvis shift above (rather than before, like it used to) means
        // a grip point that's a descendant of the skeleton (moves with bodyPosition)
        // reports its already-shifted position, not a stale pre-shift one. That
        // staleness was the actual cause of hands visibly detaching from the
        // weapon when the pelvis dropped -- not the pelvis shift itself.
        ApplyBothHandIK();
    }

    private void ApplyBothHandIK()
    {
        ApplyHandIK(AvatarIKGoal.LeftHand, AvatarIKHint.LeftElbow, HumanBodyBones.LeftHand, _leftHandIKTarget, _leftHandIKHint, _leftHandIKState, _leftHandIKTransitionDurationOverride);
        ApplyHandIK(AvatarIKGoal.RightHand, AvatarIKHint.RightElbow, HumanBodyBones.RightHand, _rightHandIKTarget, _rightHandIKHint, _rightHandIKState, _rightHandIKTransitionDurationOverride);
    }

    private void ApplyPeek()
    {
        Transform spineBone = _animator.GetBoneTransform(HumanBodyBones.Spine);
        if (spineBone == null)
            return;

        Transform upperChestBone = _animator.GetBoneTransform(HumanBodyBones.UpperChest);

        float targetPeekOffset = -movement.PeekAmount * peekMaxOffset;
        _currentPeekOffset = Mathf.Lerp(_currentPeekOffset, targetPeekOffset, peekBendSpeed * Time.deltaTime);

        // Split evenly -- UpperChest is a child of Spine, so its own share adds
        // to whatever it already inherited from Spine's shift, cascading to the
        // full offset by UpperChest instead of one rigid hinge at the base.
        Vector3 shiftPerBone = movement.transform.right * (_currentPeekOffset / 2f);

        spineBone.position += shiftPerBone;
        if (upperChestBone != null)
            upperChestBone.position += shiftPerBone;
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
            _animator.SetIKHintPosition(hint, hintTarget.position + Vector3.up * _currentPelvisOffset);

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

        // bodyPosition adjustments made earlier this same OnAnimatorIK don't
        // propagate to bone/attachment Transforms (like a grip point on the
        // weapon) until AFTER OnAnimatorIK returns -- so target.position read here
        // is always last frame's pose regardless of call order. _currentPelvisOffset
        // itself is a plain float updated synchronously above, so adding it here is
        // the only way to account for this frame's pelvis shift before it's applied.
        _animator.SetIKPosition(goal, state.CurrentPosition + Vector3.up * _currentPelvisOffset);
        _animator.SetIKRotation(goal, state.CurrentRotation);
    }

    private void ApplyFootIK(AvatarIKGoal goal, RaycastHit hitInfo, float weight, ref float smoothedHeight)
    {
        _animator.SetIKPositionWeight(goal, weight);
        _animator.SetIKRotationWeight(goal, weight);

        if (weight <= 0f)
            return;

        float targetHeight = hitInfo.point.y + footIKGroundOffset;
        smoothedHeight = Mathf.Lerp(smoothedHeight, targetHeight, footIKHeightSmoothSpeed * Time.deltaTime);

        _animator.SetIKPosition(goal, new Vector3(hitInfo.point.x, smoothedHeight, hitInfo.point.z));

        Quaternion targetRotation = Quaternion.FromToRotation(Vector3.up, hitInfo.normal) * _animator.GetIKRotation(goal);
        _animator.SetIKRotation(goal, targetRotation);
    }
}

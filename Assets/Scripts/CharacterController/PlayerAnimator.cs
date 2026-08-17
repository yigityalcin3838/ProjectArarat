using UnityEngine;
using UnityEngine.Animations.Rigging;

[RequireComponent(typeof(Animator))]
[DefaultExecutionOrder(50)]
public class PlayerAnimator : MonoBehaviour
{
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private PlayerLook look;
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

    [Header("Ladder Aim Lock")]
    [SerializeField] private MultiAimConstraint spineAim;
    [SerializeField] private MultiAimConstraint chestAim;
    [SerializeField] private MultiAimConstraint upperChestAim;

    [Header("Peek")]
    [SerializeField] private float peekMaxAngle = 20f;
    [SerializeField] private float peekBendSpeed = 8f;

    [Header("Car Turn Lag")]
    [SerializeField] private float carTurnLagSpeed = 6f;
    [SerializeField] private float carTurnLagMaxAngle = 30f;

    [Header("Hand IK")]
    [SerializeField] private float handIKTransitionDuration = 0.08f;

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

    private Animator _animator;
    private float _facingOffset;
    private bool _isRecoveringFromTurn;
    private float _currentPelvisOffset;
    private float _leftFootHeight;
    private float _rightFootHeight;
    private float _spineAimBaseWeight;
    private float _chestAimBaseWeight;
    private float _upperChestAimBaseWeight;
    private float _currentPeekAngle;
    private Quaternion _smoothedCarRotation;
    private Transform _leftHandIKTarget;
    private Transform _rightHandIKTarget;
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

        _leftFootHeight = _animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y;
        _rightFootHeight = _animator.GetBoneTransform(HumanBodyBones.RightFoot).position.y;

        if (spineAim != null) _spineAimBaseWeight = spineAim.weight;
        if (chestAim != null) _chestAimBaseWeight = chestAim.weight;
        if (upperChestAim != null) _upperChestAimBaseWeight = upperChestAim.weight;

        _smoothedCarRotation = movement.transform.rotation;
    }

    private void Update()
    {
        _animator.SetBool(IsClimbingLadderHash, movement.IsClimbingLadder);
        _animator.SetFloat(ClimbSpeedHash, movement.IsClimbingLadder ? movement.MoveInput.y : 0f);
        _animator.SetBool(IsInCarHash, movement.IsInCar);

        UpdateLadderAimLock();

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

    public void SetLeftHandIKTarget(Transform target, float? transitionDuration = null)
    {
        _leftHandIKTarget = target;
        _leftHandIKTransitionDurationOverride = transitionDuration;
    }

    public void SetRightHandIKTarget(Transform target, float? transitionDuration = null)
    {
        _rightHandIKTarget = target;
        _rightHandIKTransitionDurationOverride = transitionDuration;
    }

    public void ClearHandIKTargets()
    {
        _leftHandIKTarget = null;
        _rightHandIKTarget = null;
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
        if (_animator.IsInTransition(0))
        {
            AnimatorStateInfo nextInfo = _animator.GetNextAnimatorStateInfo(0);
            isInState = nextInfo.IsName(stateName);
            return nextInfo;
        }

        AnimatorStateInfo currentInfo = _animator.GetCurrentAnimatorStateInfo(0);
        isInState = currentInfo.IsName(stateName);
        return currentInfo;
    }

    private void UpdateLadderAimLock()
    {
        float multiplier = movement.IsClimbingLadder || movement.IsInCar ? 0f : 1f;

        if (spineAim != null) spineAim.weight = _spineAimBaseWeight * multiplier;
        if (chestAim != null) chestAim.weight = _chestAimBaseWeight * multiplier;
        if (upperChestAim != null) upperChestAim.weight = _upperChestAimBaseWeight * multiplier;
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != 0)
            return;

        ApplyHandIK(AvatarIKGoal.LeftHand, HumanBodyBones.LeftHand, _leftHandIKTarget, _leftHandIKState, _leftHandIKTransitionDurationOverride);
        ApplyHandIK(AvatarIKGoal.RightHand, HumanBodyBones.RightHand, _rightHandIKTarget, _rightHandIKState, _rightHandIKTransitionDurationOverride);

        if (movement.IsClimbingLadder || movement.IsInCar)
        {
            _animator.SetIKPositionWeight(AvatarIKGoal.LeftFoot, 0f);
            _animator.SetIKRotationWeight(AvatarIKGoal.LeftFoot, 0f);
            _animator.SetIKPositionWeight(AvatarIKGoal.RightFoot, 0f);
            _animator.SetIKRotationWeight(AvatarIKGoal.RightFoot, 0f);
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
    }

    private void ApplyPeek()
    {
        Transform spineBone = _animator.GetBoneTransform(HumanBodyBones.Spine);
        if (spineBone == null)
            return;

        float targetPeekAngle = -movement.PeekAmount * peekMaxAngle;
        _currentPeekAngle = Mathf.Lerp(_currentPeekAngle, targetPeekAngle, peekBendSpeed * Time.deltaTime);

        spineBone.localRotation *= Quaternion.Euler(0f, 0f, _currentPeekAngle);
    }

    private void ApplyHandIK(AvatarIKGoal goal, HumanBodyBones handBone, Transform target, HandIKState state, float? transitionDurationOverride)
    {
        float weight = target != null ? 1f : 0f;
        _animator.SetIKPositionWeight(goal, weight);
        _animator.SetIKRotationWeight(goal, weight);

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

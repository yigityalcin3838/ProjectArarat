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

    private void Awake()
    {
        _animator = GetComponent<Animator>();

        _leftFootHeight = _animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y;
        _rightFootHeight = _animator.GetBoneTransform(HumanBodyBones.RightFoot).position.y;

        if (spineAim != null) _spineAimBaseWeight = spineAim.weight;
        if (chestAim != null) _chestAimBaseWeight = chestAim.weight;
        if (upperChestAim != null) _upperChestAimBaseWeight = upperChestAim.weight;
    }

    private void Update()
    {
        _animator.SetBool(IsClimbingLadderHash, movement.IsClimbingLadder);
        _animator.SetFloat(ClimbSpeedHash, movement.IsClimbingLadder ? movement.MoveInput.y : 0f);

        UpdateLadderAimLock();

        if (movement.IsClimbingLadder)
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
            AnimatorStateInfo info = GetLadderFinishStateInfo(out bool isInLadderFinish);
            return isInLadderFinish ? Mathf.Clamp01(info.normalizedTime) : 0f;
        }
    }

    private void OnAnimatorMove()
    {
        GetLadderFinishStateInfo(out bool isInLadderFinish);

        if (isInLadderFinish && movement != null)
            movement.ApplyLadderFinishMotion(_animator.deltaPosition);
    }

    private AnimatorStateInfo GetLadderFinishStateInfo(out bool isInLadderFinish)
    {
        if (_animator.IsInTransition(0))
        {
            AnimatorStateInfo nextInfo = _animator.GetNextAnimatorStateInfo(0);
            isInLadderFinish = nextInfo.IsName("LadderClimbFinish");
            return nextInfo;
        }

        AnimatorStateInfo currentInfo = _animator.GetCurrentAnimatorStateInfo(0);
        isInLadderFinish = currentInfo.IsName("LadderClimbFinish");
        return currentInfo;
    }

    private void UpdateLadderAimLock()
    {
        float multiplier = movement.IsClimbingLadder ? 0f : 1f;

        if (spineAim != null) spineAim.weight = _spineAimBaseWeight * multiplier;
        if (chestAim != null) chestAim.weight = _chestAimBaseWeight * multiplier;
        if (upperChestAim != null) upperChestAim.weight = _upperChestAimBaseWeight * multiplier;
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != 0)
            return;

        if (movement.IsClimbingLadder)
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

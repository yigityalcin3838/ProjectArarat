using UnityEngine;

[RequireComponent(typeof(Animator))]
public class PlayerAnimator : MonoBehaviour
{
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private PlayerLook look;
    [SerializeField] private float turnSpeed = 720f;
    [SerializeField] private float forwardAmountDampTime = 0.15f;
    [SerializeField] private float turnInPlaceToleranceAngle = 90f;
    [SerializeField] private float turnAnimResetSpeed = 400f;
    [SerializeField] private float lookBendAmount = 0.3f;
    [SerializeField] private LayerMask groundLayer = ~0;
    [SerializeField] private float footIKRayDistance = 0.5f;
    [SerializeField] private float footIKGroundOffset = 0.05f;
    [SerializeField] private float footIKMaxOffset = 0.15f;
    [SerializeField] private float footIKHeightSmoothSpeed = 10f;
    [SerializeField] private float pelvisAdjustSpeed = 8f;

    private static readonly int ForwardAmountHash = Animator.StringToHash("ForwardAmount");
    private static readonly int IsCrouchingHash = Animator.StringToHash("IsCrouching");
    private static readonly int TurnLeftHash = Animator.StringToHash("TurnLeft");
    private static readonly int TurnRightHash = Animator.StringToHash("TurnRight");

    private Animator _animator;
    private Transform _spine;
    private Transform _chest;
    private Transform _head;
    private float _facingOffset;
    private bool _isRecoveringFromTurn;
    private float _currentPelvisOffset;
    private float _leftFootHeight;
    private float _rightFootHeight;

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _spine = _animator.GetBoneTransform(HumanBodyBones.Spine);
        _chest = _animator.GetBoneTransform(HumanBodyBones.Chest);
        _head = _animator.GetBoneTransform(HumanBodyBones.Head);

        _leftFootHeight = _animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y;
        _rightFootHeight = _animator.GetBoneTransform(HumanBodyBones.RightFoot).position.y;
    }

    private void Update()
    {
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
    }

    private void LateUpdate()
    {
        float pitchSegment = look.Pitch * lookBendAmount / 2f;
        float yawSegment = -_facingOffset / 3f;
        _spine.Rotate(pitchSegment, yawSegment, 0f, Space.Self);
        _chest.Rotate(pitchSegment, yawSegment, 0f, Space.Self);
        _head.Rotate(0f, yawSegment, 0f, Space.Self);
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != 0)
            return;

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

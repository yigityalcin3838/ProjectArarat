using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Animator))]
public class PlayerAnimator : MonoBehaviour
{
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private PlayerLook look;
    [SerializeField] private float turnSpeed = 720f;
    [SerializeField] private float forwardAmountDampTime = 0.15f;
    [SerializeField] private float lookBendAmount = 0.3f;
    [SerializeField] private float weaponLookBendAmount = 0f;
    [SerializeField] private string upperBodyLayerName = "UpperBody";
    [SerializeField] private LayerMask groundLayer = ~0;
    [SerializeField] private float footIKRayDistance = 0.5f;
    [SerializeField] private float footIKGroundOffset = 0.05f;
    [SerializeField] private float footIKMaxOffset = 0.15f;
    [SerializeField] private float pelvisAdjustSpeed = 8f;

    private static readonly int ForwardAmountHash = Animator.StringToHash("ForwardAmount");
    private static readonly int IsCrouchingHash = Animator.StringToHash("IsCrouching");

    private Animator _animator;
    private Transform _spine;
    private Transform _chest;
    private Transform _head;
    private float _facingOffset;
    private int _upperBodyLayerIndex;
    private bool _hasWeapon;
    private float _currentPelvisOffset;

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _spine = _animator.GetBoneTransform(HumanBodyBones.Spine);
        _chest = _animator.GetBoneTransform(HumanBodyBones.Chest);
        _head = _animator.GetBoneTransform(HumanBodyBones.Head);
        _upperBodyLayerIndex = _animator.GetLayerIndex(upperBodyLayerName);
        _animator.SetLayerWeight(_upperBodyLayerIndex, 0f);
    }

    private void Update()
    {
        Vector2 moveDir = movement.MoveInput;
        float speed = moveDir.magnitude;

        float targetFacingOffset = 0f;
        float forwardAmount = 0f;

        if (speed > 0.05f)
        {
            bool isBackward = moveDir.y < 0f;
            bool useRunAnimation = movement.IsSprinting || !movement.IsGrounded;
            float sprintMultiplier = useRunAnimation ? 2f : 1f;
            forwardAmount = (isBackward ? -speed : speed) * sprintMultiplier;

            float rawAngle = Mathf.Atan2(moveDir.x, moveDir.y) * Mathf.Rad2Deg;
            float referenceAngle = isBackward ? (rawAngle >= 0f ? 180f : -180f) : 0f;
            targetFacingOffset = rawAngle - referenceAngle;
        }

        _facingOffset = Mathf.MoveTowardsAngle(_facingOffset, targetFacingOffset, turnSpeed * Time.deltaTime);
        transform.localRotation = Quaternion.Euler(0f, _facingOffset, 0f);

        _animator.SetFloat(ForwardAmountHash, forwardAmount, forwardAmountDampTime, Time.deltaTime);
        _animator.SetBool(IsCrouchingHash, movement.IsCrouching);

        if (Keyboard.current != null && Keyboard.current.nKey.wasPressedThisFrame)
        {
            _hasWeapon = !_hasWeapon;
            _animator.SetLayerWeight(_upperBodyLayerIndex, _hasWeapon ? 1f : 0f);
        }
    }

    private void LateUpdate()
    {
        float currentLookBendAmount = _hasWeapon ? weaponLookBendAmount : lookBendAmount;

        float yawSegment = -_facingOffset / 3f;
        float pitchSegment = look.Pitch * currentLookBendAmount / 2f;

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

        ApplyFootIK(AvatarIKGoal.LeftFoot, leftHitInfo, leftWeight);
        ApplyFootIK(AvatarIKGoal.RightFoot, rightHitInfo, rightWeight);
    }

    private void ApplyFootIK(AvatarIKGoal goal, RaycastHit hitInfo, float weight)
    {
        _animator.SetIKPositionWeight(goal, weight);
        _animator.SetIKRotationWeight(goal, weight);

        if (weight <= 0f)
            return;

        _animator.SetIKPosition(goal, hitInfo.point + Vector3.up * footIKGroundOffset);

        Quaternion targetRotation = Quaternion.FromToRotation(Vector3.up, hitInfo.normal) * _animator.GetIKRotation(goal);
        _animator.SetIKRotation(goal, targetRotation);
    }
}

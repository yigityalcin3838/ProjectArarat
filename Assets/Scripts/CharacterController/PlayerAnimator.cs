using UnityEngine;

[RequireComponent(typeof(Animator))]
public class PlayerAnimator : MonoBehaviour
{
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private PlayerLook look;
    [SerializeField] private float turnSpeed = 720f;
    [SerializeField] private float forwardAmountDampTime = 0.15f;
    [SerializeField] private float lookBendAmount = 0.3f;

    private static readonly int ForwardAmountHash = Animator.StringToHash("ForwardAmount");

    private Animator _animator;
    private Transform _spine;
    private Transform _chest;
    private Transform _head;
    private float _facingOffset;

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _spine = _animator.GetBoneTransform(HumanBodyBones.Spine);
        _chest = _animator.GetBoneTransform(HumanBodyBones.Chest);
        _head = _animator.GetBoneTransform(HumanBodyBones.Head);
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
            float sprintMultiplier = movement.IsSprinting ? 2f : 1f;
            forwardAmount = (isBackward ? -speed : speed) * sprintMultiplier;

            float rawAngle = Mathf.Atan2(moveDir.x, moveDir.y) * Mathf.Rad2Deg;
            float referenceAngle = isBackward ? (rawAngle >= 0f ? 180f : -180f) : 0f;
            targetFacingOffset = rawAngle - referenceAngle;
        }

        _facingOffset = Mathf.MoveTowardsAngle(_facingOffset, targetFacingOffset, turnSpeed * Time.deltaTime);
        transform.localRotation = Quaternion.Euler(0f, _facingOffset, 0f);

        _animator.SetFloat(ForwardAmountHash, forwardAmount, forwardAmountDampTime, Time.deltaTime);
    }

    private void LateUpdate()
    {
        float yawSegment = -_facingOffset / 3f;
        float pitchSegment = look.Pitch * lookBendAmount / 2f;

        _spine.Rotate(pitchSegment, yawSegment, 0f, Space.Self);
        _chest.Rotate(pitchSegment, yawSegment, 0f, Space.Self);
        _head.Rotate(0f, yawSegment, 0f, Space.Self);
    }
}

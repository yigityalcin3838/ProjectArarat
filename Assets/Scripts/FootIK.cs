using UnityEngine;

/// <summary>
/// Puts a humanoid's feet on the ground that is actually there.
///
/// A locomotion clip is authored on a flat floor, so on anything else the feet are
/// wrong in both directions at once: sunk into an upslope, hanging over a downslope,
/// and level while the surface is not. This traces under each foot and moves the goal
/// to what it finds, then drops the pelvis far enough that the lower leg can still
/// reach without the character doing the splits.
///
/// Nothing here knows what it is attached to. It reads the Animator and the world and
/// nothing else, so the same component serves the player and an enemy -- the two want
/// identical behaviour and would otherwise be two implementations drifting apart.
///
/// Requires IK Pass on the Animator Controller's base layer. Without it OnAnimatorIK
/// is never called and this does nothing at all, silently.
/// </summary>
[RequireComponent(typeof(Animator))]
public class FootIK : MonoBehaviour
{
    [Header("Weights")]
    // How much of the correction is applied. 1 is the feet fully on the ground.
    // Anything driving this from outside -- a ragdoll, a death, a climb -- should use
    // Weight below rather than turning the component off, so it fades instead of
    // popping.
    [SerializeField, Range(0f, 1f)] private float positionWeight = 1f;

    // Rotation is separate and usually wants to be lower. Putting a foot at the right
    // height is nearly always right; matching its angle to the surface is a stylistic
    // choice, and at 1 a foot on a steep slope reads as glued rather than standing.
    [SerializeField, Range(0f, 1f)] private float rotationWeight = 1f;

    [Header("Trace")]
    // Started above the foot so a foot already inside geometry still finds the
    // surface it is inside of, rather than missing everything and snapping straight.
    [SerializeField] private float traceStartHeight = 0.5f;

    // How far below the foot to look. This is the whole of what "predictable" means
    // on stairs: keep it near one step's height and a foot over the edge of a step
    // finds the tread it is leaving rather than the floor two flights down.
    [SerializeField] private float traceDistance = 0.6f;

    // Thickness of the trace. A ray through the exact gap between two treads finds
    // nothing and drops the foot; a sphere lands on the nearer edge, which is where a
    // real foot would be. The single biggest difference between foot IK that survives
    // a staircase and foot IK that does not.
    [SerializeField] private float traceRadius = 0.08f;

    // From the foot bone down to the sole. Almost every rig has the ankle some way
    // above the floor, and without this the ankles are planted and the feet are
    // buried.
    [SerializeField] private float soleOffset = 0.12f;

    [Header("Limits")]
    // How far a foot may be lifted or dropped from where the clip put it, in metres.
    //
    // The limit is what keeps a bad trace from becoming a bad pose. Something read
    // wrongly -- a step edge, a prop, a gap -- is then worth a few centimetres of
    // error rather than a leg thrown out at full stretch, and the failure looks like
    // slightly wrong footing instead of a broken character.
    [SerializeField] private float maxLift = 0.4f;
    [SerializeField] private float maxDrop = 0.5f;

    // Surfaces steeper than this are found but not turned onto. On a staircase the
    // riser is a vertical face, and a foot that rotated to match it would stand on
    // the wall of the step; on a slope past what anyone could stand on, matching the
    // angle only makes the pose look more wrong, not less. The height still comes
    // from the hit -- only the angle is refused.
    [SerializeField] private float maxSurfaceAngle = 50f;

    [Header("Smoothing")]
    // Seconds for a foot to reach a new height. Short enough that a foot does not
    // trail the ground it is standing on, long enough that a step edge crossing under
    // it is a movement rather than a snap.
    [SerializeField] private float footSmoothTime = 0.08f;

    // The pelvis wants more, because it carries the whole body and any twitch in it
    // is visible everywhere at once -- where a single foot being slightly late is not
    // visible at all.
    [SerializeField] private float pelvisSmoothTime = 0.15f;

    /// <summary>
    /// Multiplies both weights. For anything that has to take the feet away -- a
    /// ladder, a vehicle, a death -- without switching the component off and losing
    /// the fade.
    /// </summary>
    public float Weight { get; set; } = 1f;

    private Animator _animator;

    // Per foot, in world units of height, smoothed. Kept between frames because the
    // smoothing is the point: these are the values the ground is read into, not the
    // reading itself.
    private float _leftOffset;
    private float _leftOffsetVelocity;
    private float _rightOffset;
    private float _rightOffsetVelocity;

    private float _pelvisOffset;
    private float _pelvisOffsetVelocity;

    private Quaternion _leftRotation = Quaternion.identity;
    private Quaternion _rightRotation = Quaternion.identity;

    private void Awake() => _animator = GetComponent<Animator>();

    // Called once per layer that has IK Pass ticked. Only the base layer's call is
    // wanted -- the others would solve the same feet again from the same data, which
    // is not twice as correct, just twice.
    private void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != 0 || _animator == null || !_animator.isHuman)
            return;

        float weight = Mathf.Clamp01(Weight);
        if (weight <= 0f)
        {
            // Bled back to nothing rather than dropped, so whatever turned this off
            // does not also leave the feet mid-correction the moment it turns it on
            // again.
            Relax();
            return;
        }

        bool leftGrounded = SolveFoot(AvatarIKGoal.LeftFoot,
            ref _leftOffset, ref _leftOffsetVelocity, ref _leftRotation);
        bool rightGrounded = SolveFoot(AvatarIKGoal.RightFoot,
            ref _rightOffset, ref _rightOffsetVelocity, ref _rightRotation);

        // Only ever down, and only as far as the lower foot needs.
        //
        // Lifting the pelvis is the ground's job, not this one's -- the capsule is
        // already standing the character on the surface, and adding height here would
        // fight it. What the capsule cannot do is notice that one foot has further to
        // reach than the other, which is exactly what a slope is.
        float targetPelvis = 0f;
        if (leftGrounded || rightGrounded)
        {
            float lowest = Mathf.Min(
                leftGrounded ? _leftOffset : 0f,
                rightGrounded ? _rightOffset : 0f);

            targetPelvis = Mathf.Min(0f, lowest);
        }

        _pelvisOffset = Mathf.SmoothDamp(
            _pelvisOffset, targetPelvis, ref _pelvisOffsetVelocity, pelvisSmoothTime);

        _animator.bodyPosition += Vector3.up * (_pelvisOffset * weight);

        ApplyFoot(AvatarIKGoal.LeftFoot, _leftOffset, _leftRotation, leftGrounded ? weight : 0f);
        ApplyFoot(AvatarIKGoal.RightFoot, _rightOffset, _rightRotation, rightGrounded ? weight : 0f);
    }

    // Traces under one foot and eases that foot's height and angle toward what it
    // finds. Returns whether there was anything to stand on at all.
    private bool SolveFoot(AvatarIKGoal goal, ref float offset, ref float offsetVelocity,
        ref Quaternion rotation)
    {
        Vector3 animatedPosition = _animator.GetIKPosition(goal);
        Vector3 origin = animatedPosition + Vector3.up * traceStartHeight;

        // The same exclusions everything else in the project uses: the movement
        // capsule, which wraps the character's own legs and would have every foot
        // standing on its owner; and debris, which is walked through rather than on.
        if (!Physics.SphereCast(origin, traceRadius, Vector3.down, out RaycastHit hit,
                traceStartHeight + traceDistance, GameLayers.Queryable,
                QueryTriggerInteraction.Ignore))
        {
            // Eased back to the clip rather than dropped to it, so walking off an edge
            // is the foot returning to its animation over footSmoothTime instead of
            // jumping there on one frame.
            offset = Mathf.SmoothDamp(offset, 0f, ref offsetVelocity, footSmoothTime);
            rotation = Quaternion.identity;
            return false;
        }

        float targetOffset = Mathf.Clamp(
            (hit.point.y + soleOffset) - animatedPosition.y, -maxDrop, maxLift);

        offset = Mathf.SmoothDamp(offset, targetOffset, ref offsetVelocity, footSmoothTime);

        // Refused rather than clamped. A surface too steep to stand on has no angle
        // worth borrowing, and a partial rotation toward a stair riser is just a
        // smaller version of the same wrong pose.
        rotation = Vector3.Angle(hit.normal, Vector3.up) <= maxSurfaceAngle
            ? Quaternion.FromToRotation(Vector3.up, hit.normal)
            : Quaternion.identity;

        return true;
    }

    private void ApplyFoot(AvatarIKGoal goal, float offset, Quaternion surfaceRotation, float weight)
    {
        _animator.SetIKPositionWeight(goal, weight * positionWeight);
        _animator.SetIKRotationWeight(goal, weight * rotationWeight);

        _animator.SetIKPosition(goal, _animator.GetIKPosition(goal) + Vector3.up * offset);
        _animator.SetIKRotation(goal, surfaceRotation * _animator.GetIKRotation(goal));
    }

    // Everything back toward the clip, without writing a goal. Nothing is solved on
    // these frames, so the stored offsets are all that has to come home -- and they
    // have to, or switching back on would resume from wherever the character was
    // standing when it switched off.
    private void Relax()
    {
        _leftOffset = Mathf.SmoothDamp(_leftOffset, 0f, ref _leftOffsetVelocity, footSmoothTime);
        _rightOffset = Mathf.SmoothDamp(_rightOffset, 0f, ref _rightOffsetVelocity, footSmoothTime);
        _pelvisOffset = Mathf.SmoothDamp(_pelvisOffset, 0f, ref _pelvisOffsetVelocity, pelvisSmoothTime);

        _leftRotation = Quaternion.identity;
        _rightRotation = Quaternion.identity;

        _animator.SetIKPositionWeight(AvatarIKGoal.LeftFoot, 0f);
        _animator.SetIKRotationWeight(AvatarIKGoal.LeftFoot, 0f);
        _animator.SetIKPositionWeight(AvatarIKGoal.RightFoot, 0f);
        _animator.SetIKRotationWeight(AvatarIKGoal.RightFoot, 0f);
    }
}

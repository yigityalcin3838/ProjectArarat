using UnityEngine;

// A golem that is scenery until it isn't.
//
// It sits as a rock playing its wait pose, stands up when the player comes close
// enough, then walks them down and swings. That order is the whole behaviour, and
// each step owns the character completely while it runs -- rising cannot be walked
// out of, a swing cannot be steered. Anything that reads as weight comes from those
// commitments, so they are enforced here rather than left to the animator to
// suggest.
//
// Movement is driven in code against a CharacterController rather than by a
// NavMeshAgent, because there is no baked NavMesh in the scene -- an agent would
// fail to place itself and the golem would never move at all. Straight-line pursuit
// is the trade: it will walk into a wall rather than around one. Once a NavMesh is
// baked, MoveTowardsTarget is the one method that has to change.
[RequireComponent(typeof(CharacterController))]
public class EnemyGolem : MonoBehaviour
{
    private enum State
    {
        // Curled up and inert. Not looking at anything, not turning -- a golem that
        // tracked the player while still pretending to be a rock would give itself
        // away before the reveal.
        Dormant,

        // Standing up. Deliberately uninterruptible: this is the animation that
        // sells what the thing is, and cutting it short to start walking would
        // waste it.
        Rising,

        Chasing,

        // Mid-swing. Also uninterruptible, and also not steerable -- a golem that
        // could rotate to follow the player through its own swing would land every
        // blow no matter what the player did, which removes the only counterplay a
        // slow heavy attack has.
        Attacking,

        // The player has left. Standing, doing nothing, and still willing to be
        // interrupted -- coming back within range here picks the chase straight back
        // up. This is the grace period, not the decision.
        LosingInterest,

        // Going back to being scenery. Uninterruptible like Rising, and for the same
        // reason: it is the mirror of that animation and cutting it short would leave
        // the golem half-collapsed.
        Settling,
    }

    [Header("Target")]
    // Found automatically when left empty, so a golem dropped into the scene works
    // without being wired up. Serialized anyway for the cases where it should chase
    // something that isn't the player.
    [SerializeField] private Transform target;

    [Header("Animator")]
    [SerializeField] private Animator animator;

    // State names rather than trigger parameters, so this drives the animator
    // through CrossFade and needs no parameters or transitions authored at all.
    // The script already knows the exact order things happen in; expressing that a
    // second time as a graph of conditions would only add a place for the two to
    // disagree.
    [SerializeField] private string waitState = "Anim_Wait";
    [SerializeField] private string riseState = "Anim_Rise";
    [SerializeField] private string idleState = "Anim_Idle";
    [SerializeField] private string walkState = "Anim_Walk";
    [SerializeField] private string[] attackStates = { "Anim_Attack1", "Anim_Attack2" };

    // Played on the way back to dormant, not on being killed -- a golem is not killed
    // by anything that plays a clip, it comes apart. What this animation actually
    // shows is a standing body collapsing, which is exactly the return to being a
    // rock, so it is used for that rather than left unused for a death that never
    // needs it.
    [SerializeField] private string settleState = "Anim_Death";

    // The heavy one, kept out of attackStates because it is not one of the ordinary
    // swings -- it is what interrupts them on a schedule.
    [SerializeField] private string aoeAttackState = "Anim_Attack_AoE";

    [Header("Senses")]
    // How close the player has to get before it wakes. Measured on the horizontal
    // plane only -- a player on a rooftop directly overhead is not "close" in any
    // sense the golem can act on, and a strict 3D distance would wake it anyway.
    [SerializeField] private float wakeRadius = 12f;

    // Seconds of standing about after the player leaves that radius, before the golem
    // gives up and settles back down.
    //
    // The delay is the hysteresis. A golem that dropped the instant the player
    // stepped over the line would stand up and fall down repeatedly for anyone
    // walking along it, and re-entering during the wait simply resumes the chase --
    // so the boundary costs nothing to cross and only leaving properly ends the
    // fight.
    [SerializeField] private float loseInterestDelay = 4f;

    [Header("Movement")]
    [SerializeField] private float walkSpeed = 2.2f;


    // Degrees per second. Slow on purpose: turning is the golem's real weakness, and
    // it is what lets a player circle behind one.
    [SerializeField] private float turnSpeed = 120f;

    [Header("Attack")]
    // Where a swing starts. Wants to be a little longer than it looks, since the
    // animation reaches further than the collider suggests.
    //
    // It gates the animation, not damage -- nothing here touches the player yet. What
    // it buys is that the golem stops and swings when it arrives rather than walking
    // into you still swinging, which is the difference between reading as an attack
    // and reading as a loop.
    [SerializeField] private float attackRange = 2.8f;

    // Kept apart from attackRange so the golem does not flicker between walking and
    // swinging on the boundary: once attacking, the player has to get this much
    // further away before it goes back to walking.
    [SerializeField] private float attackRangeHysteresis = 0.6f;

    [SerializeField] private float attackCooldown = 1.2f;

    // Ordinary swings between one AoE and the next. Counted rather than rolled at
    // random, because the point of it is that the player can learn it: two swings
    // are survivable at close range and the third is not, so the pattern is the
    // instruction to back off. A dice roll would teach nothing and occasionally
    // land two AoEs in a row.
    //
    // Zero switches the AoE off entirely.
    [SerializeField] private int attacksBeforeAoe = 2;

    [Header("Hit Reaction")]
    // How far a shot turns whatever bone it lands on, across every hitbox at once.
    //
    // One figure for the whole body because that is the size of the actual decision:
    // how heavily this character reacts to being shot. The spring, the damping and
    // the ceiling stay per-hitbox and describe the joint; this describes the golem.
    //
    // Zero holds every bone still without disabling anything else -- hits are still
    // counted and parts still come off.
    [SerializeField] private float boneRecoilScale = 1f;

    [Header("Debug")]
    // Prints every state change this script asks the animator for, and what the
    // animator was actually playing at the time. Off by default; ticking it is the
    // fastest way to tell "the script never asked for the walk" apart from "it asked
    // and the animator ignored it", which look identical from outside.
    [SerializeField] private bool logAnimationRequests;

    // Rises when approached and then does nothing further -- no walking, no swinging.
    // A target that holds still, for testing anything that acts on the body while
    // the body is not also trying to close distance and hit back.
    [SerializeField] private bool standStillAfterRise;

    private CharacterController _controller;
    private State _state = State.Dormant;
    private float _stateTimer;
    private float _cooldownTimer;
    private float _verticalVelocity;
    private int _lastAttackIndex = -1;
    private int _swingsSinceAoe;
    private bool _isInAttackRange;
    private bool _isDead;

    // Gathered once. Hitboxes are never added or removed at runtime -- a severed one
    // is disabled and left in place -- so the array stays valid for the whole fight.
    private EnemyHitbox[] _hitboxes;

    // How fast the walk clip itself travels, read out of the animation rather than
    // typed in. Zero when the clip carries no root motion, which means there is no
    // authored speed to match and the walk is played at its own rate.
    private float _walkClipSpeed;

    // Nothing left to animate or move. Called when the body has come apart, which is
    // its own death animation -- there is no Anim_Death here on purpose, because
    // playing a clip on a skeleton whose bones have all been collapsed to nothing
    // would animate a body that is no longer there.
    public void Kill()
    {
        if (_isDead)
            return;

        _isDead = true;

        // The animator goes quiet rather than being destroyed, so the collapsed
        // bones stay collapsed instead of being restored by the next clip evaluation.
        if (animator != null)
            animator.enabled = false;

        if (_controller != null)
            _controller.enabled = false;

        enabled = false;
    }

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _hitboxes = GetComponentsInChildren<EnemyHitbox>(includeInactive: true);

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (target == null)
        {
            PlayerMovement player = FindFirstObjectByType<PlayerMovement>();
            if (player != null)
                target = player.transform;
        }

        // This script owns where the golem is. Root motion would have the clips
        // move it as well, and the two would fight -- most visibly as a walk that
        // drifts at some speed unrelated to walkSpeed.
        if (animator != null)
            animator.applyRootMotion = false;
    }

    private void Start()
    {
        ValidateStateNames();
        ResolveWalkClipSpeed();

        // In Start rather than Awake: the animator has to have run its own
        // initialisation before a CrossFade will take, and cross-object Awake order
        // is not guaranteed.
        Play(waitState);
    }

    // CrossFade to a name the controller does not have is a silent no-op: no error,
    // no warning, the animator simply keeps playing whatever it was on. The golem
    // then walks along in its idle pose and nothing anywhere says why.
    //
    // That is a bad failure for a field that is typed by hand and has to match an
    // asset exactly, so it is checked once at startup and named out loud instead.
    private void ValidateStateNames()
    {
        WarnIfStateMissing(waitState);
        WarnIfStateMissing(riseState);
        WarnIfStateMissing(idleState);
        WarnIfStateMissing(walkState);
        WarnIfStateMissing(settleState);

        // Only when it is actually going to be used -- a golem deliberately set to
        // never AoE should not be nagged about a name it will never ask for.
        if (attacksBeforeAoe > 0)
            WarnIfStateMissing(aoeAttackState);

        if (attackStates == null)
            return;

        foreach (string attack in attackStates)
            WarnIfStateMissing(attack);
    }

    private void WarnIfStateMissing(string stateName)
    {
        if (animator == null || string.IsNullOrEmpty(stateName))
            return;

        if (animator.HasState(0, Animator.StringToHash(stateName)))
            return;

        // Called out separately because it is the mistake this actually catches in
        // practice, and the one hardest to see by eye: state name matching is a hash
        // comparison, so a single wrong capital is as unmatchable as a wrong word,
        // while the two names look identical at a glance in the Inspector.
        string caseHint = FindCaseInsensitiveMatch(stateName);

        Debug.LogError(
            $"EnemyGolem on '{name}': the Animator Controller has no state called " +
            $"'{stateName}'. That animation will never play." +
            (caseHint != null
                ? $" It has '{caseHint}' though -- state names are case-sensitive, so fix the capitals."
                : " State names are the names in the controller, which for this pack are the clip" +
                  " names (Anim_Wait, Anim_Rise, Anim_Idle, Anim_Walk, Anim_Attack1, Anim_Attack2)."),
            this);
    }

    private void Update()
    {
        // Pushed every frame rather than once at startup, so the slider stays live
        // while playing -- the same reason Weapon re-sends its aim rig weights each
        // frame. Seventeen assignments is nothing next to having to re-enter Play
        // Mode to see a number change.
        //
        // Dormant is the one state where the golem is scenery rather than an enemy,
        // so nothing done to it should move it or take it apart. Rising is already
        // past that: once it has started standing up it is a body, and shooting it
        // mid-rise counts.
        bool isInert = _state == State.Dormant;

        foreach (EnemyHitbox hitbox in _hitboxes)
        {
            hitbox.RecoilScale = boneRecoilScale;
            hitbox.IsInert = isInert;
        }

        if (_cooldownTimer > 0f)
            _cooldownTimer -= Time.deltaTime;

        switch (_state)
        {
            case State.Dormant:
                UpdateDormant();
                break;

            case State.Rising:
                UpdateRising();
                break;

            case State.Chasing:
                UpdateChasing();
                break;

            case State.Attacking:
                UpdateAttacking();
                break;

            case State.LosingInterest:
                UpdateLosingInterest();
                break;

            case State.Settling:
                UpdateSettling();
                break;
        }

        ApplyGravity();
    }

    private void UpdateDormant()
    {
        if (target == null || HorizontalDistanceToTarget() > wakeRadius)
            return;

        EnterState(State.Rising, GetStateLength(riseState));
        Play(riseState);
    }

    private void UpdateRising()
    {
        _stateTimer -= Time.deltaTime;

        if (_stateTimer > 0f)
            return;

        EnterState(State.Chasing, 0f);
        Play(idleState);
    }

    private void UpdateChasing()
    {
        // Checked before the target, so it holds still even with nothing to chase.
        if (standStillAfterRise)
        {
            Play(idleState);
            return;
        }

        if (target == null)
            return;

        float distance = HorizontalDistanceToTarget();

        // Out of range, so start the countdown rather than dropping on the spot. The
        // same radius that woke it is the one it loses the player at, which keeps the
        // golem's attention exactly as far as its notice.
        if (distance > wakeRadius)
        {
            _isInAttackRange = false;
            EnterState(State.LosingInterest, loseInterestDelay);
            Play(idleState);
            return;
        }

        // Hysteresis on the ring rather than one threshold: a player standing right
        // on the boundary would otherwise flip the golem between walking and
        // standing every other frame. Getting in takes attackRange; getting back
        // out takes noticeably more.
        _isInAttackRange = _isInAttackRange
            ? distance <= attackRange + attackRangeHysteresis
            : distance <= attackRange;

        if (_isInAttackRange && _cooldownTimer <= 0f)
        {
            BeginAttack();
            return;
        }

        // Turning happens whether or not there is room to walk, so a golem stood on
        // top of the player still faces them while it waits out its cooldown.
        FaceTarget();

        if (_isInAttackRange)
        {
            Play(idleState);
            return;
        }

        Play(walkState);
        MoveTowardsTarget();
    }

    private void UpdateLosingInterest()
    {
        // Checked before the timer, so coming back cancels it outright rather than
        // being noticed once it has run out.
        if (target != null && HorizontalDistanceToTarget() <= wakeRadius)
        {
            EnterState(State.Chasing, 0f);
            return;
        }

        _stateTimer -= Time.deltaTime;

        if (_stateTimer > 0f)
            return;

        EnterState(State.Settling, GetStateLength(settleState));
        Play(settleState);
    }

    private void UpdateSettling()
    {
        _stateTimer -= Time.deltaTime;

        if (_stateTimer > 0f)
            return;

        // Back to the start. Walking into the radius again wakes it exactly as it did
        // the first time, so a golem can be woken, escaped, and woken again -- it is
        // a loop rather than a one-off.
        EnterState(State.Dormant, 0f);
        Play(waitState);
    }

    private void UpdateAttacking()
    {
        _stateTimer -= Time.deltaTime;

        if (_stateTimer > 0f)
            return;

        _cooldownTimer = attackCooldown;
        EnterState(State.Chasing, 0f);
        Play(idleState);
    }

    private void BeginAttack()
    {
        string attack = PickAttack();

        EnterState(State.Attacking, GetStateLength(attack));
        Play(attack);
    }

    // Never the same swing twice running, which is what a plain Random.Range gives
    // often enough to notice. With two attacks this alternates; with more it picks
    // freely among the rest.
    //
    // Every attacksBeforeAoe swings the AoE takes the slot instead, and does not
    // count as one of them -- so the cycle is two ordinary swings then the heavy
    // one, over and over, rather than the AoE eating into its own count.
    private string PickAttack()
    {
        if (attacksBeforeAoe > 0
            && _swingsSinceAoe >= attacksBeforeAoe
            && !string.IsNullOrEmpty(aoeAttackState))
        {
            _swingsSinceAoe = 0;
            return aoeAttackState;
        }

        _swingsSinceAoe++;

        if (attackStates == null || attackStates.Length == 0)
            return idleState;

        if (attackStates.Length == 1)
            return attackStates[0];

        int index = Random.Range(0, attackStates.Length - 1);
        if (index >= _lastAttackIndex)
            index++;

        _lastAttackIndex = index;
        return attackStates[index];
    }

    private void FaceTarget()
    {
        Vector3 toTarget = target.position - transform.position;
        toTarget.y = 0f;

        if (toTarget.sqrMagnitude < 0.0001f)
            return;

        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            Quaternion.LookRotation(toTarget),
            turnSpeed * Time.deltaTime);
    }

    // Walks along its own forward rather than straight at the target, so the slow
    // turn above is actually felt: a golem that has not finished turning yet walks
    // wide, the way something that heavy would.
    private void MoveTowardsTarget()
    {
        _controller.Move(transform.forward * (walkSpeed * Time.deltaTime));
    }

    // Physics.gravity, which SceneGravity sets from the scene -- the same source
    // PlayerMovement falls by. A CharacterController is moved by hand rather than by
    // the physics engine, so gravity has to be integrated here, but the figure it
    // integrates should still be the scene's rather than a second one on this
    // component that could be left disagreeing with it.
    private void ApplyGravity()
    {
        if (_controller.isGrounded && _verticalVelocity < 0f)
        {
            // A small hold-down rather than zero. Exactly zero has isGrounded
            // flicker on slopes, and a flickering ground check reads as the golem
            // stuttering as it walks.
            _verticalVelocity = -2f;
        }
        else
        {
            _verticalVelocity += Physics.gravity.y * Time.deltaTime;
        }

        _controller.Move(Vector3.up * (_verticalVelocity * Time.deltaTime));
    }

    private float HorizontalDistanceToTarget()
    {
        Vector3 delta = target.position - transform.position;
        delta.y = 0f;
        return delta.magnitude;
    }

    private void EnterState(State state, float duration)
    {
        _state = state;
        _stateTimer = duration;
    }

    // Guarded against restarting a state that is already playing or on its way in,
    // because this is called every frame from the chase loop.
    //
    // Both halves matter. While a crossfade runs, the animator's "current" state is
    // still the one being faded OUT -- the destination does not become current until
    // the blend finishes, and until then it is only reachable through
    // GetNextAnimatorStateInfo. Asking about the current state alone therefore
    // answers "not playing yet" for the entire blend, so a per-frame caller
    // restarts the fade every frame and it never completes: the walk sits at a
    // sliver of its weight forever and the legs never move.
    //
    // Rise and the attacks are each requested once, so they were never affected --
    // which is exactly why walking was the only thing visibly broken.
    private void Play(string stateName)
    {
        if (animator == null || string.IsNullOrEmpty(stateName))
            return;

        // Set before the guards, on every request rather than only on the ones that
        // change state. The speed has to hold for as long as the state does, and the
        // chase loop asks for the walk every frame -- so the request that changes
        // nothing is exactly the one keeping this current.
        //
        // animator.speed rather than a speed parameter on the state, because this
        // controller deliberately has no parameters. Only one state plays at a time,
        // so scaling the whole animator is scaling that state.
        animator.speed = string.Equals(stateName, walkState) && _walkClipSpeed > 0.01f
            ? walkSpeed / _walkClipSpeed
            : 1f;

        if (animator.IsInTransition(0))
        {
            if (animator.GetNextAnimatorStateInfo(0).IsName(stateName))
                return;
        }
        else if (animator.GetCurrentAnimatorStateInfo(0).IsName(stateName))
        {
            return;
        }

        if (logAnimationRequests)
        {
            AnimatorStateInfo playing = animator.GetCurrentAnimatorStateInfo(0);
            Debug.Log(
                $"[Golem f{Time.frameCount}] state={_state} asking for '{stateName}' " +
                $"(exists={animator.HasState(0, Animator.StringToHash(stateName))}) " +
                $"while playing hash={playing.shortNameHash} " +
                $"inTransition={animator.IsInTransition(0)}",
                this);
        }

        // Fixed time rather than normalised: a normalised blend is a fraction of the
        // *destination* clip's length, so the same value would make a short swing
        // blend briefly and a long rise blend for nearly a second.
        animator.CrossFadeInFixedTime(stateName, 0.15f);
    }

    // Read off the controller by clip name, the same way Weapon does, so there are
    // no duration fields to keep in step with the animations by hand.
    //
    // Returns zero for a name the controller has no clip for, which the callers
    // treat as "already finished" -- a missing rise is skipped rather than hung on.
    private float GetStateLength(string stateName)
    {
        AnimationClip clip = GetClip(stateName);

        return clip != null ? clip.length : 0f;
    }

    private AnimationClip GetClip(string stateName)
    {
        if (animator == null || animator.runtimeAnimatorController == null)
            return null;

        foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
        {
            if (clip.name == stateName)
                return clip;
        }

        return null;
    }

    // How far the walk clip actually travels per second, taken from the animation
    // instead of being typed in beside it.
    //
    // averageSpeed is the clip's own root motion averaged over its length, which IS
    // the speed the stride was drawn for -- so the ratio against walkSpeed keeps the
    // feet planted, and walkSpeed can then be changed freely without the walk falling
    // apart. A hand-entered figure would be the same number guessed a second time,
    // and would quietly stop matching the moment the clip was reimported.
    //
    // Horizontal only: a walk that rises and falls slightly should not read as
    // travelling faster for it.
    private void ResolveWalkClipSpeed()
    {
        AnimationClip clip = GetClip(walkState);
        if (clip == null)
            return;

        Vector3 velocity = clip.averageSpeed;
        _walkClipSpeed = new Vector2(velocity.x, velocity.z).magnitude;

        if (_walkClipSpeed > 0.01f)
            return;

        // In-place clip. There is no authored speed to match, so the walk plays at
        // its own rate and the feet will slide at any walkSpeed that is not the one
        // it happens to suit. Fixable in the model importer rather than here, which
        // is why it says where to look.
        Debug.LogWarning(
            $"EnemyGolem on '{name}': the walk clip '{walkState}' has no root motion, so its " +
            "speed cannot be read and the stride cannot be matched to walkSpeed. Set a Root " +
            "Motion Node on the model's Rig import settings if the feet slide.", this);
    }

    // The animator can only be asked whether a specific hash exists, never for the
    // list of names behind those hashes -- so the candidates come from the clips the
    // controller references, which for a controller of plain states are the same
    // names. Good enough to spot a wrong capital, which is all this is for.
    private string FindCaseInsensitiveMatch(string stateName)
    {
        if (animator.runtimeAnimatorController == null)
            return null;

        foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
        {
            if (!string.Equals(clip.name, stateName, System.StringComparison.OrdinalIgnoreCase))
                continue;

            // Only a hint if that spelling is genuinely a state, since a clip name
            // and its state name are free to differ.
            if (animator.HasState(0, Animator.StringToHash(clip.name)))
                return clip.name;
        }

        return null;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, wakeRadius);

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, attackRange);
    }
}

using UnityEngine;

// One collider on one bone, saying which part of the body it is.
//
// This is identity and nothing else. It does not apply damage, does not move the
// bone, does not detach anything -- there is no damage system yet, and a routing
// API written before there is anything to route would be guesswork that the first
// real caller has to undo. What it does is make every future feature a lookup
// instead of a search: a raycast returns a Collider, GetComponent finds this, and
// this says "left shoulder, on that golem".
//
// Everything planned for these hangs off exactly that answer:
//
//   recoil        the struck part is known, so the bone to push is known
//   dismemberment the struck part is known, so the piece to detach is known, and
//                 the hit count lives per-part rather than per-enemy
//   armouring     damageMultiplier below, once damage exists
//
// Not a trigger. The weapon raycasts with QueryTriggerInteraction.Ignore (so a shot
// is not eaten by a door's interaction volume), which means a trigger hitbox would
// be invisible to gunfire -- the one thing it exists to catch. Being solid instead
// is why these want their own physics layer with every collision-matrix box
// unticked: raycasts ignore that matrix and still find them, while the golem stops
// tripping over its own arms.
[RequireComponent(typeof(Collider))]
public class EnemyHitbox : MonoBehaviour
{
    [SerializeField] private EnemyBodyPart bodyPart;

    // Multiplies whatever damage eventually arrives. Serialized now, unused now --
    // it is the one number that has to be authored per collider rather than derived,
    // and gathering it later would mean revisiting every hitbox on every enemy.
    [SerializeField] private float damageMultiplier = 1f;

    // The enemy this belongs to. Resolved rather than assigned, because these are
    // generated onto bones several levels deep and hand-wiring seventeen of them per
    // enemy is seventeen chances to wire one to the wrong golem.
    [SerializeField] private Transform owner;

    [Header("Recoil")]
    // Down the bone, in the bone's own space, pointing away from the joint toward
    // the part's mass. Written by EnemyHitboxBuilder, which already measures it to
    // orient the capsule -- so the recoil gets the bone's long axis for free instead
    // of guessing which axis the exporter used.
    [SerializeField] private Vector3 boneAxis = Vector3.right;

    // Knocked aside and back inside about half a second, with one clear overshoot.
    // The stiffness is what makes the part snap rather than sag; the damping is what
    // stops it ringing afterwards, since a part that wobbles on reads as light and
    // this one is rock.
    [SerializeField] private float recoilSpring = 25f;
    [SerializeField] private float recoilDamping = 7f;

    // Degrees, and the whole of what bounds a burst. Small enough that the ceiling
    // is reached constantly rather than exceptionally -- which is fine, and is why
    // the speed still driving a part outward is spent when it arrives there rather
    // than stored (see LateUpdate). Without that, twelve shotgun pellets landing on
    // one frame would pin the part against the limit holding all of it, and unload
    // the lot on the way back.
    [SerializeField] private float maxRecoilAngle = 60f;

    // How much of a hit is handed on to the part above this one in the skeleton.
    //
    // A shoulder does not float free of the chest -- a round heavy enough to knock
    // it back turns the whole torso with it, and a body that absorbs a hit entirely
    // in the one bone that was struck looks jointed rather than solid.
    //
    // It applies up the whole chain, not just to the torso, because the same is true
    // everywhere: a hand takes the forearm with it, the forearm the upper arm, and
    // so on. Each step multiplies again, so a hit four bones away arrives at the
    // spine at a few percent and fades out on its own rather than needing a rule
    // about where to stop.
    [SerializeField, Range(0f, 1f)] private float impactPropagation = 0.3f;

    // Whether being shot here MOVES this part. Not whether it can be hurt by it --
    // the hit is registered either way, and this decides only what the body does
    // about it.
    //
    // Off for the torso. A round into a limb has something to lever against and the
    // limb gives; a round into the middle of a chest has nothing to turn about, and
    // a body that rocks when shot centre-mass reads as light. It stays the anchor
    // the rest of the body swings from -- which is also why a direct hit here stops
    // dead rather than travelling on to the pelvis: moving the hips would swing
    // everything above them, which is the same wobble one bone further down.
    //
    // Two things this is NOT. It does not make the part immune, and it is not about
    // hits arriving from elsewhere: the torso still turns when a shoulder passes an
    // impact up to it, which is the point of that chain.
    [SerializeField] private bool takesDirectRecoil = true;

    [Header("Dismemberment")]
    // Kept as an escape hatch for a part that should never come off, though nothing
    // currently uses it: refusing to be moved by a hit and refusing to be destroyed
    // by one are separate, and the core parts want the first without the second.
    [SerializeField] private bool canDetach = true;

    // Direct hits before this part comes off. Counted here rather than on the enemy
    // because it is per-part: shooting a shoulder twice takes the shoulder, and does
    // nothing at all toward taking the other one.
    [SerializeField] private int hitsToDetach = 2;

    // Losing this part ends the fight rather than damaging it. Set on the head and
    // on the legs: a golem cannot go on without a head, and cannot stand on one leg.
    // Everything else is survivable, which is what makes an arm worth shooting off
    // as a tactic rather than as a slower way of winning.
    [SerializeField] private bool detachIsFatal;

    private int _directHits;
    private bool _isDetached;

    // Axis-angle, carried as one vector: the direction is the axis to turn about and
    // the magnitude is how far, in degrees. Kept this way rather than as Euler
    // angles because the impulse arrives as an axis and a spring on Euler triples
    // does not stay on the axis it started on.
    private Vector3 _recoilOffset;
    private Vector3 _recoilVelocity;

    public EnemyBodyPart BodyPart => bodyPart;
    public float DamageMultiplier => damageMultiplier;

    // Direct hits this part has taken, counted for every part including the ones
    // that refuse to move. What a damage system reads when there is one.
    public int DirectHits => _directHits;

    // Scales how far a hit turns this bone. Set from outside rather than serialized
    // here, so one figure on the enemy governs all seventeen -- tuning how heavily a
    // character reacts is a decision about the character, not about each of its
    // bones, and seventeen sliders to answer one question is seventeen chances for
    // them to disagree.
    public float RecoilScale { get; set; } = 1f;

    // Set while the enemy is not a body yet -- a golem curled up in its wait pose is
    // a rock, and a rock does not flinch or come apart. Shots still land on it and
    // still throw sparks; they simply do not move it.
    //
    // Hits are not counted either. Counting them would mean a golem could be quietly
    // whittled down while it slept and lose an arm on its second shot after standing
    // up, which is a fight decided before it started.
    public bool IsInert { get; set; }

    // The bone this hitbox rides. The thing a recoil would push and a detachment
    // would break off, kept separate from the hitbox's own transform so the collider
    // can be offset from the joint without moving the bone.
    public Transform Bone => transform.parent;

    public Transform Owner
    {
        get
        {
            if (owner == null)
                owner = ResolveOwner();

            return owner;
        }
    }

    private void Awake()
    {
        owner = ResolveOwner();
    }

    // Fires a hit at this part from the component's context menu, with no weapon
    // involved.
    //
    // Shooting at a golem tests two independent things at once -- whether a bullet
    // reaches this collider at all, and whether the recoil that follows works -- and
    // when nothing moves, the two look identical from behind the gun. This exercises
    // only the second, so a part that moves here and not under fire narrows the
    // problem to the raycast, and one that moves in neither narrows it to the spring.
    //
    // Right-click the component header while in Play Mode.
    [ContextMenu("Test Recoil")]
    private void TestRecoil()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("Test Recoil only does anything in Play Mode.", this);
            return;
        }

        // Straight into the golem's front, which is where a shot would come from
        // when the player is the thing it is walking at.
        Vector3 direction = Owner != null ? -Owner.forward : -transform.forward;

        ApplyImpact(direction, 800f);
        Debug.Log($"Test recoil on {bodyPart}: bone='{(Bone != null ? Bone.name : "NONE")}' axis={boneAxis}", this);
    }

    // Struck by something. Direction is where the hit was travelling, so the part is
    // pushed the way the bullet was already going.
    public void ApplyImpact(Vector3 worldDirection, float force)
    {
        ApplyImpact(worldDirection, force, isDirectHit: true);
    }

    // The two cases are kept apart only so takesDirectRecoil can tell them apart --
    // a part can refuse to be moved by a round landing on it while still being
    // turned by one that landed further out on a limb.
    private void ApplyImpact(Vector3 worldDirection, float force, bool isDirectHit)
    {
        if (Bone == null || force <= 0f || _isDetached || IsInert)
            return;

        if (isDirectHit)
        {
            // Registered first, before anything can decline to react. Refusing to be
            // moved is not the same as refusing to be hit, and conflating the two is
            // how a torso ends up bulletproof: the count below is what damage will
            // read, and the chest is the easiest thing on the body to hit.
            _directHits++;

            if (canDetach && _directHits >= hitsToDetach)
            {
                Detach();
                return;
            }

            // From here down is only about movement. Stopping here leaves the hit
            // recorded and the part still.
            if (!takesDirectRecoil)
                return;
        }

        Vector3 localPush = Bone.InverseTransformDirection(worldDirection.normalized);

        // The cross product is doing real work here, not just producing an axis. Its
        // length is the sine of the angle between the shot and the bone, which is
        // exactly the leverage the shot has: a round straight down the length of an
        // arm barely turns it, one across the arm turns it most. Normalising this
        // would throw that away and make every angle hit equally hard.
        Vector3 axis = Vector3.Cross(boneAxis.normalized, localPush);

        _recoilVelocity += axis * (force * RecoilScale);

        // The unscaled force is what travels on, so each part up the chain applies
        // the scale once to its own impulse rather than compounding it at every
        // joint.
        //
        // Passed on in world space, so each part resolves it against its own bone --
        // the leverage a shot has on a clavicle is not the leverage it has on the
        // spine, and converting once here would carry the wrong one upward.
        if (impactPropagation > 0f)
            ParentHitbox?.ApplyImpact(worldDirection, force * impactPropagation, isDirectHit: false);
    }

    // In LateUpdate because the Animator rewrites every bone during the animation
    // update, which runs before this and after Update. An offset applied any earlier
    // is simply overwritten by the clip and nothing moves.
    //
    // Post-multiplied onto whatever the animation just produced rather than replacing
    // it, so the part recoils *while* continuing to be animated -- a golem shot
    // mid-swing keeps swinging with a shoulder that has been knocked back.
    private void LateUpdate()
    {
        if (Bone == null || _isDetached)
            return;

        // Damped spring. An impulse on the velocity throws the part out and lets it
        // return past centre and settle; easing a value toward zero cannot do that,
        // and a recoil that never overshoots reads as a part sliding rather than
        // being struck.
        _recoilVelocity += (-recoilSpring * _recoilOffset - recoilDamping * _recoilVelocity) * Time.deltaTime;
        _recoilOffset += _recoilVelocity * Time.deltaTime;

        Vector3 clamped = Vector3.ClampMagnitude(_recoilOffset, maxRecoilAngle);
        if (clamped != _recoilOffset)
        {
            // Held against the ceiling, so the speed that was still driving it
            // outward is spent rather than stored. Clamping the angle alone leaves
            // that speed intact behind the limit, and it all comes back the moment
            // the spring turns the part around -- a part that looks stuck for a
            // moment and then snaps.
            //
            // Only the outward part goes. Whatever motion was across the limit is
            // still real and keeps running.
            Vector3 outward = clamped.normalized;
            float outwardSpeed = Vector3.Dot(_recoilVelocity, outward);

            if (outwardSpeed > 0f)
                _recoilVelocity -= outward * outwardSpeed;

            _recoilOffset = clamped;
        }

        float angle = _recoilOffset.magnitude;
        if (angle < 0.01f)
            return;

        Bone.localRotation *= Quaternion.AngleAxis(angle, _recoilOffset / angle);
    }

    private void Detach()
    {
        EnemyDismemberment dismemberment = Owner != null
            ? Owner.GetComponent<EnemyDismemberment>()
            : null;

        if (dismemberment == null)
        {
            Debug.LogWarning(
                $"EnemyHitbox on '{name}' reached its detach threshold, but the enemy has no " +
                "EnemyDismemberment component to do it. Add one to the enemy root.", this);
            return;
        }

        // Marked before either call, because both of them read this: a shatter walks
        // every hitbox, and the one that triggered it must already count as gone or
        // it would be cut a second time.
        foreach (EnemyHitbox below in Bone.GetComponentsInChildren<EnemyHitbox>(includeInactive: true))
            below.MarkDetached();

        if (detachIsFatal)
        {
            dismemberment.Shatter();
            return;
        }

        dismemberment.DetachPart(Bone);
    }

    // Left in place rather than destroyed, so the piece's history is still readable
    // and a future respawn has something to switch back on.
    private void MarkDetached()
    {
        _isDetached = true;
        _recoilOffset = Vector3.zero;
        _recoilVelocity = Vector3.zero;

        foreach (Collider collider in GetComponents<Collider>())
            collider.enabled = false;
    }

    private EnemyHitbox _parentHitbox;
    private bool _hasResolvedParent;

    // The next hitbox up the skeleton -- a shoulder's is the torso, a hand's is the
    // forearm. Null at the top of the chain, which is a real answer and why the
    // lookup is guarded by a flag rather than by the cache being null.
    private EnemyHitbox ParentHitbox
    {
        get
        {
            if (!_hasResolvedParent)
            {
                _parentHitbox = FindParentHitbox();
                _hasResolvedParent = true;
            }

            return _parentHitbox;
        }
    }

    // Read off the skeleton rather than from a table of which part sits above which.
    // The bones already encode it exactly and cannot fall out of step with
    // themselves, whereas a table would have to be corrected by hand every time a
    // part was added or a rig changed.
    //
    // Hitboxes hang beside their bone rather than on it, so this looks at each
    // ancestor's direct children rather than at the ancestors themselves. Direct
    // children only: searching deeper from a spine bone would reach down an arm and
    // find a hand, which is below this part, not above it.
    private EnemyHitbox FindParentHitbox()
    {
        for (Transform ancestor = Bone != null ? Bone.parent : null;
             ancestor != null;
             ancestor = ancestor.parent)
        {
            foreach (Transform child in ancestor)
            {
                if (child.TryGetComponent(out EnemyHitbox hitbox))
                    return hitbox;
            }
        }

        return null;
    }

    // Walks up to whatever is driving this skeleton. The Animator marks the root of
    // a character reliably in a way a tag or a layer does not -- bones have neither,
    // and the hitboxes hang off bones.
    private Transform ResolveOwner()
    {
        Animator animator = GetComponentInParent<Animator>();

        return animator != null ? animator.transform : transform.root;
    }

#if UNITY_EDITOR
    // Toggled from Tools > Enemy > Show Hitboxes. Static rather than a field on each
    // hitbox, because seventeen per enemy means a per-instance checkbox is seventeen
    // things to tick to answer one question.
    public static bool DrawAlways;

    private void OnDrawGizmos()
    {
        if (DrawAlways)
            DrawShape();
    }

    private void OnDrawGizmosSelected()
    {
        if (!DrawAlways)
            DrawShape();
    }

    // Coloured by side, not by importance: left and right are the distinction that
    // actually has to be readable at a glance, since the whole point of these is
    // being able to say "that was the LEFT shoulder" while looking at the thing.
    private Color PartColor()
    {
        string part = bodyPart.ToString();

        if (bodyPart == EnemyBodyPart.Head)
            return new Color(1f, 0.25f, 0.25f);

        if (part.StartsWith("Left"))
            return new Color(0.35f, 0.6f, 1f);

        if (part.StartsWith("Right"))
            return new Color(1f, 0.65f, 0.2f);

        return new Color(0.4f, 1f, 0.5f);
    }

    // Drawn from the collider rather than from the bone measurements, so what shows
    // up is what the raycast will actually hit -- including any nudging done by hand
    // afterwards. A gizmo that redrew the intended shape instead of the real one
    // would hide exactly the mistakes it is meant to reveal.
    private void DrawShape()
    {
        Color color = PartColor();

        if (!TryGetComponent(out BoxCollider box))
            return;

        // A box needs no mesh and no normalising -- Gizmos draws cubes natively, in
        // the collider's own units. The earlier capsule version had to scale a
        // built-in mesh and got it wrong by assuming that mesh's dimensions; there is
        // nothing left here to be wrong about.
        Gizmos.matrix = transform.localToWorldMatrix;

        // Solid first, then the wireframe over it at full strength. The translucent
        // body shows the volume and the outline gives it an edge -- without the
        // outline, seventeen overlapping transparent shapes read as one fog.
        Gizmos.color = new Color(color.r, color.g, color.b, 0.25f);
        Gizmos.DrawCube(box.center, box.size);

        Gizmos.color = color;
        Gizmos.DrawWireCube(box.center, box.size);

        Gizmos.matrix = Matrix4x4.identity;
    }
#endif
}

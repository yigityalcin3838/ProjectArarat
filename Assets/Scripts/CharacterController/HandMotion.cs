using UnityEngine;

// Bob and sway for the hands, applied once to whatever transform every item's
// grips hang off rather than per-item. It used to live on the weapon, which meant
// every new item re-tuned the same numbers and two items could disagree about how
// the same pair of hands moves. Nothing here knows what is being held.
//
// Writes its own transform: the base pose is read once at Awake and the offsets
// are rebuilt from it every frame, never accumulated onto what is already there.
// That is what keeps two effects on one transform from fighting -- and what lets
// a breathing or kick layer be added later the same way.
//
// After PlayerLook, so the phase read below is this frame's rather than last
// frame's. A stale phase would only be a fixed sliver of a cycle behind, but it
// costs nothing to be exact about it.
[DefaultExecutionOrder(60)]
public class HandMotion : MonoBehaviour
{
    // One figure per gait, for whichever of the two things it is describing: how
    // far a layer moves, or how fast it settles.
    //
    // Written out per gait rather than derived from a speed ratio. A ratio is one
    // dial and it decides all three together -- crouching can only ever be a scaled
    // walk and a sprint the same walk scaled the other way. Crouching wants tight
    // and slow, a sprint wants wide and loose, and those are not the same curve
    // read at two points. Three numbers say it directly and nothing has to be
    // fought to get them apart.
    [System.Serializable]
    private struct GaitValues
    {
        public float crouch;
        public float walk;
        public float sprint;

        public GaitValues(float crouch, float walk, float sprint)
        {
            this.crouch = crouch;
            this.walk = walk;
            this.sprint = sprint;
        }
    }

    [SerializeField] private PlayerMovement movement;

    // The camera's bob phase is the walk cycle, and the hands read it rather than
    // running a clock of their own. Two clocks at the same rate stay together; two
    // clocks at different rates are not synchronised by definition, which is why
    // there is no bob frequency here. Cadence is one decision for the whole player
    // and it is made in PlayerLook. Amplitude and settle are what this component
    // decides, and those can differ freely without costing anything.
    [SerializeField] private PlayerLook look;

    [Header("Bob")]
    // Metres and degrees at a walk. Roll carries the most of it: a swinging arm
    // rocks a weapon side to side far more than it lifts or turns it, and leaning
    // on that one axis buys weight without the sights wandering off the middle.
    [SerializeField] private float bobHorizontalAmount = 0.025f;
    [SerializeField] private float bobVerticalAmount = 0.035f;
    [SerializeField] private float bobPitchAmount = 3f;
    [SerializeField] private float bobYawAmount = 4f;
    [SerializeField] private float bobRollAmount = 6f;

    // Smoothing is a catch-up rate, so higher is tighter, not slower. Crouching
    // gets the tightest of the three and a sprint the loosest -- the pace is the
    // whole difference between a placed step and a thrown one.
    [SerializeField] private GaitValues bobIntensity = new GaitValues(0.35f, 1f, 2f);
    [SerializeField] private GaitValues bobSmoothing = new GaitValues(12f, 9f, 6f);

    [Header("Strafe Sway")]
    // The movement half of the lag, against the look half further down. Horizontal
    // and forward come from intent rather than velocity: the hands trail the
    // direction being asked for, and that trailing should start on the key press
    // rather than once the body has already got moving.
    //
    // Vertical has no such input -- nothing is pressed to go up -- so it comes off
    // falling speed instead, which makes it a different animal: it drops the hands
    // on a jump and snaps them back on a landing. That is an impact rather than a
    // lag, which is why it defaults to nothing. Turn it up only if a landing wants
    // weight here rather than in a kick layer of its own.
    //
    // Kept at zero for one more reason besides: the controller holds a small
    // downward velocity while grounded to stay stuck to the floor, so any amount
    // here is a permanent droop before it is ever a landing.
    [SerializeField] private float swayHorizontalAmount = 0.05f;
    [SerializeField] private float swayVerticalAmount = 0f;
    [SerializeField] private float swayForwardAmount = 0.04f;
    [SerializeField] private GaitValues swayIntensity = new GaitValues(0.4f, 1f, 1.8f);
    [SerializeField] private GaitValues swaySmoothing = new GaitValues(10f, 7f, 5f);

    [Header("Strafe Tilt")]
    // Degrees of roll into the direction being strafed, and roll only. A sidestep
    // banks the weapon; it does not point it anywhere else, and that is the whole
    // difference between this and the look tilt below.
    //
    // PlayerLook rolls the camera for the same reason, but far less: the view
    // leaning is a suggestion of a lean, the hands leaning is the lean itself, so
    // the two are tuned apart rather than sharing a figure.
    [SerializeField] private float tiltAmount = 14f;
    [SerializeField] private GaitValues tiltIntensity = new GaitValues(0.5f, 1f, 1.6f);
    [SerializeField] private GaitValues tiltSmoothing = new GaitValues(10f, 8f, 6f);

    [Header("Look Sway")]
    // The hands lagging a turn, which is what gives a weapon any sense of mass.
    // Measured off how fast the view is turning rather than how far the mouse
    // moved, so it is the same at any frame rate and stops dead the moment the
    // view hits a pitch or yaw limit.
    //
    // Travel only. The turning half of the same lag is the tilt below, and it keeps
    // its own rate and filter -- a slide and a cant answer the same mouse but not
    // on the same terms, and sharing the figures would mean tuning one of them by
    // ruining the other.
    [SerializeField] private float lookSwayHorizontalAmount = 0.06f;
    [SerializeField] private float lookSwayVerticalAmount = 0.045f;

    // Degrees per second that produces the full amount above. A brisk flick is
    // several hundred; anything past this is clamped, so a violent turn displaces
    // the hands no further than a firm one.
    [SerializeField] private float lookSwayReferenceRate = 240f;

    [SerializeField] private GaitValues lookSwayIntensity = new GaitValues(0.6f, 1f, 1.5f);

    // Its own filter rather than sharing the movement sway's: raw mouse deltas are
    // spiky in a way a key press never is, and the two want different amounts of
    // smoothing to sit still.
    [SerializeField] private GaitValues lookSwaySmoothing = new GaitValues(16f, 12f, 9f);

    [Header("Look Tilt")]
    // The point the tilt turns about. A marker, not a parent: nothing has to hang
    // off it, and it can sit anywhere in the scene. Its position is read once at
    // startup and kept as a fixed point in this transform's own space, so the
    // marker can even be moved or deleted afterwards without the maths noticing.
    //
    // It matters because a rotation is only ever as interesting as the distance
    // between its centre and what is turning around it. Put it at the grip and the
    // weapon turns in place; set it back behind the shoulder and the muzzle swings
    // out. The point that looks right is rarely the one the bob happens to be
    // written from, and this is how the two stop having to be the same point.
    //
    // Empty means the transform's own resting position, which is a turn in place.
    [SerializeField] private Transform lookPivot;

    // Degrees on each axis, because what should trail a turn here is the muzzle.
    // Roll alone can't do that -- it spins the weapon about its own barrel and
    // leaves the tip pointing exactly where it was. Pitch off the vertical turn and
    // yaw off the horizontal one are what swing the far end of the weapon behind
    // the view, which is the whole of what a heavy thing does when you whip the
    // camera around.
    //
    // Roll is still here for the bank on top, and signed to match the strafe tilt
    // so turning right and stepping right cant the hands the same way rather than
    // cancelling when both happen at once.
    // Yaw leads, because horizontal is what a player actually whips the mouse
    // through; pitch behind it, roll least of the three so the bank stays a
    // seasoning on the swing rather than the swing itself.
    [SerializeField] private float lookTiltPitchAmount = 6f;
    [SerializeField] private float lookTiltYawAmount = 9f;
    [SerializeField] private float lookTiltRollAmount = 5f;

    // The tilt's own rate and filter, deliberately not the sway's. A cant tends to
    // want a lower ceiling and a slower settle than a slide: the same flick that
    // should shove the hands right across should only tip them.
    [SerializeField] private float lookTiltReferenceRate = 200f;
    [SerializeField] private GaitValues lookTiltIntensity = new GaitValues(0.6f, 1f, 1.4f);
    [SerializeField] private GaitValues lookTiltSmoothing = new GaitValues(14f, 10f, 7f);

    private Vector3 _baseLocalPosition;
    private Quaternion _baseLocalRotation;
    private Vector3 _lookPivotLocalPosition;
    private Vector3 _currentLookTilt;
    private Vector3 _currentBobOffset;
    private Vector3 _currentBobRotation;
    private Vector3 _currentSway;
    private Vector3 _currentLookSway;
    private float _currentTilt;

    // Which of the three is in force. Crouch first, which costs nothing: sprinting
    // already requires not being crouched, so the two can never both be true.
    //
    // The switch is a hard one, and it is the smoothing below that absorbs it --
    // every layer's result is lerped, so a gait change slides the amplitude across
    // rather than stepping it. Blending the figures themselves as well would only
    // smooth what is already smooth.
    private float ForGait(GaitValues values)
        => movement.IsCrouching ? values.crouch
            : movement.IsSprintingStable ? values.sprint
            : values.walk;

    private void Awake()
    {
        _baseLocalPosition = transform.localPosition;
        _baseLocalRotation = transform.localRotation;

        // Brought into the same space the offsets below are built in, once, so the
        // marker's own parentage is irrelevant -- it can hang off the weapon, the
        // camera or nothing at all and still name the same point.
        _lookPivotLocalPosition = _baseLocalPosition;

        if (lookPivot != null)
        {
            _lookPivotLocalPosition = transform.parent != null
                ? transform.parent.InverseTransformPoint(lookPivot.position)
                : lookPivot.position;
        }
    }

    private void Update()
    {
        if (movement == null || look == null)
            return;

        // The same condition PlayerLook gates its camera bob on, down to using
        // the grace-period grounding rather than raw IsGrounded -- a step off a
        // kerb shouldn't stop the hands mid-cycle. Matching it is what has the
        // hands and the camera fade their bob in and out on the same frame.
        bool isMoving = movement.IsGroundedStable
            && !movement.IsMovementLocked
            && movement.MoveInput.sqrMagnitude > 0.01f;

        float bobAmount = ForGait(bobIntensity);
        float swayAmount = ForGait(swayIntensity);
        float tiltAmountForGait = ForGait(tiltIntensity);
        float lookSwayAmount = ForGait(lookSwayIntensity);
        float lookTiltAmountForGait = ForGait(lookTiltIntensity);

        float bobPhase = look.BobPhase;

        // Vertical runs at twice the horizontal: one dip per footfall against one
        // side-to-side swing per full stride. Pitch follows the vertical phase;
        // yaw and roll follow the horizontal one, a quarter cycle apart from each
        // other so the whole thing traces a circle rather than a line. Same
        // pairing the camera uses, so the two read as one motion.
        Vector3 targetBobOffset = isMoving
            ? new Vector3(
                Mathf.Cos(bobPhase) * bobHorizontalAmount * bobAmount,
                Mathf.Sin(bobPhase * 2f) * bobVerticalAmount * bobAmount,
                0f)
            : Vector3.zero;

        Vector3 targetBobRotation = isMoving
            ? new Vector3(
                Mathf.Sin(bobPhase * 2f) * bobPitchAmount * bobAmount,
                Mathf.Sin(bobPhase) * bobYawAmount * bobAmount,
                Mathf.Cos(bobPhase) * bobRollAmount * bobAmount)
            : Vector3.zero;

        // Negated: the hands trail the movement rather than leading it.
        Vector3 targetSway = new Vector3(
            -movement.MoveInput.x * swayHorizontalAmount * swayAmount,
            -movement.Velocity.y * swayVerticalAmount * swayAmount,
            -movement.MoveInput.y * swayForwardAmount * swayAmount);

        // A rate, not a per-frame amount: LookDelta is degrees this frame, which
        // doubles if the frame does. Dividing it back out is what keeps the hands
        // displaced the same distance for the same turn at any frame rate.
        Vector2 lookRate = Time.deltaTime > 0f ? look.LookDelta / Time.deltaTime : Vector2.zero;

        // Normalised against each layer's own reference rate, so the two can be
        // told apart: the flick that has already shoved the slide as far as it goes
        // can still have room left to lean.
        Vector3 targetLookSway = new Vector3(
            -Mathf.Clamp(lookRate.x / lookSwayReferenceRate, -1f, 1f) * lookSwayHorizontalAmount * lookSwayAmount,
            -Mathf.Clamp(lookRate.y / lookSwayReferenceRate, -1f, 1f) * lookSwayVerticalAmount * lookSwayAmount,
            0f);

        float lookTiltRatioX = Mathf.Clamp(lookRate.x / lookTiltReferenceRate, -1f, 1f);
        float lookTiltRatioY = Mathf.Clamp(lookRate.y / lookTiltReferenceRate, -1f, 1f);

        Vector3 targetLookTilt = new Vector3(
            -lookTiltRatioY * lookTiltPitchAmount,
            -lookTiltRatioX * lookTiltYawAmount,
            -lookTiltRatioX * lookTiltRollAmount) * lookTiltAmountForGait;

        // Nothing to lean into while a ladder or a car is driving the character:
        // the input is still being read there and would roll the hands over on a
        // key the player isn't steering with.
        float targetTilt = movement.IsMovementLocked
            ? 0f
            : -movement.MoveInput.x * tiltAmount * tiltAmountForGait;

        _currentTilt = Mathf.Lerp(_currentTilt, targetTilt, ForGait(tiltSmoothing) * Time.deltaTime);
        _currentBobOffset = Vector3.Lerp(_currentBobOffset, targetBobOffset, ForGait(bobSmoothing) * Time.deltaTime);
        _currentBobRotation = Vector3.Lerp(_currentBobRotation, targetBobRotation, ForGait(bobSmoothing) * Time.deltaTime);
        _currentSway = Vector3.Lerp(_currentSway, targetSway, ForGait(swaySmoothing) * Time.deltaTime);
        _currentLookSway = Vector3.Lerp(_currentLookSway, targetLookSway, ForGait(lookSwaySmoothing) * Time.deltaTime);
        _currentLookTilt = Vector3.Lerp(_currentLookTilt, targetLookTilt, ForGait(lookTiltSmoothing) * Time.deltaTime);

        Vector3 position = _baseLocalPosition + _currentBobOffset + _currentSway + _currentLookSway;

        // Every rotational layer summed into one Euler off the base rather than
        // each writing the transform in turn. The old per-weapon version read
        // localEulerAngles back and overwrote one channel of it, which meant
        // whichever component wrote last that frame won -- composing them here
        // leaves nothing to win, and adding the next layer is one more term.
        Quaternion rotation = _baseLocalRotation * Quaternion.Euler(
            _currentBobRotation.x,
            _currentBobRotation.y,
            _currentBobRotation.z + _currentTilt);

        // The look tilt applied last and about the marked point rather than about
        // this transform's own origin: swung around it, so the offset from the
        // pivot becomes travel and not just a turn on the spot. With the pivot at
        // the transform's own position the two are the same thing, which is why
        // that is the fallback.
        //
        // Nothing needs to be parented to the marker for this. Turning something
        // about a point is arithmetic, and the hierarchy has no say in it.
        Quaternion lookTilt = Quaternion.Euler(_currentLookTilt);
        position = _lookPivotLocalPosition + lookTilt * (position - _lookPivotLocalPosition);
        rotation = lookTilt * rotation;

        transform.localPosition = position;
        transform.localRotation = rotation;
    }
}

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
    // One figure per stance, for whichever of the two things it is describing: how
    // far a layer moves, or how fast it settles.
    //
    // Written out per stance rather than derived from a speed ratio. A ratio is one
    // dial and it decides them all together -- crouching can only ever be a scaled
    // walk and a sprint the same walk scaled the other way. Crouching wants tight
    // and small, a sprint wants wide and loose, and those are not the same curve
    // read at two points. Four numbers say it directly and nothing has to be fought
    // to get them apart.
    //
    // Aiming is in here beside the gaits despite not being one, because it behaves
    // like one: it replaces the figures outright rather than scaling them, and a
    // player down the sights wants the same steadiness whether they are crouched or
    // walking. Keeping it as a fifth thing to multiply by would only mean tuning
    // every gait twice.
    [System.Serializable]
    private struct StanceValues
    {
        public float crouch;
        public float walk;
        public float sprint;
        public float aim;

        public StanceValues(float crouch, float walk, float sprint, float aim)
        {
            this.crouch = crouch;
            this.walk = walk;
            this.sprint = sprint;
            this.aim = aim;
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
    [SerializeField] private StanceValues bobIntensity = new StanceValues(0.35f, 1f, 2f, 0.2f);
    [SerializeField] private StanceValues bobSmoothing = new StanceValues(12f, 9f, 6f, 14f);

    [Header("Breathing")]
    // The slow drift of a weapon held still. Only while the character is standing:
    // the bob takes over the moment it moves, and the two running together would be
    // a stride with a wobble laid over it rather than either one read clearly.
    //
    // Phase comes from PlayerLook, so this is the same breath the camera is taking.
    //
    // No stance column, unlike every layer below. Those exist to tell a crouch from
    // a sprint, and this never runs during either -- a standstill is the only state
    // it has, so there would be one figure in four dressed up as a choice.
    [SerializeField] private float breathHorizontalAmount = 0.004f;
    [SerializeField] private float breathVerticalAmount = 0.008f;
    [SerializeField] private float breathPitchAmount = 0.8f;
    [SerializeField] private float breathRollAmount = 0.6f;
    [SerializeField] private float breathSmoothing = 4f;

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
    [SerializeField] private StanceValues swayIntensity = new StanceValues(0.4f, 1f, 1.8f, 0.3f);
    [SerializeField] private StanceValues swaySmoothing = new StanceValues(10f, 7f, 5f, 12f);

    [Header("Strafe Tilt")]
    // Degrees of roll into the direction being strafed, and roll only. A sidestep
    // banks the weapon; it does not point it anywhere else, and that is the whole
    // difference between this and the look tilt below.
    //
    // PlayerLook rolls the camera for the same reason, but far less: the view
    // leaning is a suggestion of a lean, the hands leaning is the lean itself, so
    // the two are tuned apart rather than sharing a figure.
    [SerializeField] private float tiltAmount = 14f;
    [SerializeField] private StanceValues tiltIntensity = new StanceValues(0.5f, 1f, 1.6f, 0.3f);
    [SerializeField] private StanceValues tiltSmoothing = new StanceValues(10f, 8f, 6f, 12f);

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

    [SerializeField] private StanceValues lookSwayIntensity = new StanceValues(0.6f, 1f, 1.5f, 0.35f);

    // Its own filter rather than sharing the movement sway's: raw mouse deltas are
    // spiky in a way a key press never is, and the two want different amounts of
    // smoothing to sit still.
    [SerializeField] private StanceValues lookSwaySmoothing = new StanceValues(16f, 12f, 9f, 18f);

    [Header("Jump / Land Kick")]
    // The hands' own, and the only jump and landing they get -- the camera's version
    // is deliberately kept off the items (see PlayerLook), so without this a landing
    // would shake the view around a weapon nailed in place.
    //
    // Separate from the camera's rather than shared, because a braced weapon and a
    // head do not answer a landing alike: one is absorbed by arms and a shoulder,
    // the other by a neck.
    //
    // Impulses on velocity, not offsets: a spring flung and left to settle overshoots
    // and comes back, which is what an impact does. A lerp toward a target only ever
    // approaches it.
    [SerializeField] private float jumpKickAmount = 0.6f;
    [SerializeField] private float landKickAmount = 1.2f;
    [SerializeField] private float jumpKickPitchAmount = 30f;
    [SerializeField] private float landKickPitchAmount = 60f;

    // Roll kept smaller than pitch. A landing drives the weapon down far more than
    // it cants it, and a large roll here reads as the whole view being twisted
    // rather than as a weight settling in the hands.
    [SerializeField] private float jumpKickRollAmount = 15f;
    [SerializeField] private float landKickRollAmount = 30f;

    // One spring for all three, so they arrive and settle together -- a landing is
    // one impact, not three springs going off at their own pace.
    [SerializeField] private float kickSpring = 200f;
    [SerializeField] private float kickDamping = 20f;

    [Header("Shouldering")]
    // The weapon being re-settled against the shoulder. Fired whenever the way the
    // character is carrying itself changes: dropping into a crouch or standing back
    // up, setting off walking, raising or lowering the sights, leaning out or back.
    //
    // One impulse for all of them, not a value per event. They are the same physical
    // thing -- a grip adjusting to a body that has just moved under it -- and giving
    // each its own figure would only be the same settle written seven times, drifting
    // apart as they were tuned.
    //
    // Impulses on velocity, in metres per second and degrees per second, so the shape
    // is a throw and a recovery rather than a slide between two points. Peak travel is
    // roughly the impulse over the square root of the spring: at 220, 0.3 is about two
    // centimetres and 25 about a degree and a half.
    [SerializeField] private Vector3 shoulderingPosition = new Vector3(0f, -0.25f, 0.3f);
    [SerializeField] private Vector3 shoulderingRotation = new Vector3(-25f, 0f, 8f);
    [SerializeField] private float shoulderingSpring = 220f;
    [SerializeField] private float shoulderingDamping = 22f;

    // The view's share of the same jolt, fired at the same instant and tuned here
    // rather than in PlayerLook. One motion, one place to set it: split across two
    // components the halves would be adjusted separately and end up disagreeing
    // about a thing that only ever happens once.
    //
    // Far smaller figures than the hands'. A stance change moves a weapon held out
    // at arm's length several centimetres; it moves the head a few millimetres, and
    // matching the two would read as the camera being shoved rather than the body
    // resettling under it.
    [SerializeField] private Vector3 cameraShoulderingPosition = new Vector3(0f, -0.1f, 0.08f);
    [SerializeField] private Vector3 cameraShoulderingRotation = new Vector3(-12f, 0f, 4f);
    [SerializeField] private float cameraShoulderingSpring = 220f;
    [SerializeField] private float cameraShoulderingDamping = 22f;

    [Header("Peek")]
    // Degrees at a full lean, mirrored the other way for the other side. On top of
    // whatever the camera pivot's own peek roll already gives the weapon by carrying
    // it -- this is the hands doing something of their own, not a restatement of
    // that: bringing the weapon in tighter or turning it into the corner while the
    // view leans past it.
    //
    // Unsmoothed on purpose. PeekAmount is already eased into over peekSpeed, so
    // this follows a curve that has been shaped once rather than filtering a filter.
    [SerializeField] private Vector3 peekRotation = new Vector3(0f, 0f, -6f);

    [Header("Look Tilt")]
    // Degrees of roll into the turn -- the same bank the strafe tilt gives, off the
    // mouse instead of the keys, and roll only like it is. Signed to match it, so
    // turning right and stepping right cant the hands the same way rather than
    // cancelling when both happen at once.
    //
    // Travel on the other axes is the look sway's, which is what it is for.
    [SerializeField] private float lookTiltAmount = 5f;

    // The tilt's own rate and filter, deliberately not the sway's. A cant tends to
    // want a lower ceiling and a slower settle than a slide: the same flick that
    // should shove the hands right across should only tip them.
    [SerializeField] private float lookTiltReferenceRate = 200f;
    [SerializeField] private StanceValues lookTiltIntensity = new StanceValues(0.6f, 1f, 1.4f, 0.35f);
    [SerializeField] private StanceValues lookTiltSmoothing = new StanceValues(14f, 10f, 7f, 16f);

    private Vector3 _baseLocalPosition;
    private Quaternion _baseLocalRotation;
    private float _currentLookTilt;
    private Vector3 _currentBreathOffset;
    private Vector2 _currentBreathRotation;
    private float _kickOffset;
    private float _kickVelocity;
    private float _kickPitchOffset;
    private float _kickPitchVelocity;
    private float _kickRollOffset;
    private float _kickRollVelocity;
    private Vector3 _shoulderingPositionOffset;
    private Vector3 _shoulderingPositionVelocity;
    private Vector3 _shoulderingRotationOffset;
    private Vector3 _shoulderingRotationVelocity;
    private bool _wasCrouching;
    private bool _wasAiming;
    private bool _wasSprinting;
    private Vector3 _currentBobOffset;
    private Vector3 _currentBobRotation;
    private Vector3 _currentSway;
    private Vector3 _currentLookSway;
    private float _currentTilt;

    // Which of the four is in force. Aiming wins outright -- someone lining up a
    // shot while walking wants the steadiness of the sights, not a walk with a
    // discount. Crouch next, which costs nothing: sprinting already requires not
    // being crouched, so those two can never both be true.
    //
    // The switch is a hard one, and it is the smoothing that absorbs it -- every
    // layer's result is lerped, so a stance change slides the amplitude across
    // rather than stepping it. Blending the figures themselves as well would only
    // smooth what is already smooth.
    private float ForStance(StanceValues values)
        => movement.IsAiming ? values.aim
            : movement.IsCrouching ? values.crouch
            : movement.IsSprintingStable ? values.sprint
            : values.walk;

    // Public, because a jolt in the hands is not only a stance thing: a reload, a
    // melee, a door shouldered open all want the same spring, and all of them would
    // otherwise grow one of their own. Set rather than added, so two in quick
    // succession read as the second replacing the first instead of stacking into a
    // throw neither asked for -- which matters here, since crouching while already
    // moving fires two of these a frame apart.
    public void AddKick(Vector3 positionImpulse, Vector3 rotationImpulse)
    {
        _shoulderingPositionVelocity = positionImpulse;
        _shoulderingRotationVelocity = rotationImpulse;
    }

    // The configured shouldering jolt, hands and view together. Public so an item
    // can fire it for a moment only the item can see -- a weapon settling into its
    // walking carry, say, which happens some seconds after the legs set off and
    // nowhere near it. The figures stay here either way; what the caller supplies is
    // the timing.
    public void TriggerShouldering()
    {
        AddKick(shoulderingPosition, shoulderingRotation);
        TriggerCameraShouldering();
    }

    // The view's half on its own, for the moments where the hands are the wrong
    // place to put it. A reload ends with the weapon already being moved by its own
    // clip, and a swap with it on its way out of one hand and into the other -- a
    // spring on top of either is a second opinion about where the weapon is. The
    // head has no such animation and is free to register that something happened.
    public void TriggerCameraShouldering()
    {
        if (look == null)
            return;

        look.AddShoulderingKick(
            cameraShoulderingPosition,
            cameraShoulderingRotation,
            cameraShoulderingSpring,
            cameraShoulderingDamping);
    }

    // Edges, not states. What is wanted is the moment the character changes how it
    // is carrying itself, and a flag that is simply true for a while has two of those
    // in it -- so crouching, aiming and leaning each fire on the way in and on the way
    // out. Walking fires only on setting off: coming to a stop is the body settling,
    // not the grip being re-taken.
    private void UpdateShouldering()
    {
        bool isCrouching = movement.IsCrouching;
        bool isAiming = movement.IsAiming;

        // The same figure the stance columns are read against, so the jolt lands on
        // the frame the amplitudes change rather than a frame either side of it.
        bool isSprinting = movement.IsSprintingStable;

        // Raising or lowering the sights, dropping into a crouch or coming back up,
        // breaking into a run or falling out of one. Crouching counts down the sights
        // as much as off them -- the whole body drops half a metre either way, and a
        // weapon held against a shoulder that has just moved is exactly what this is
        // for.
        //
        // Walking and leaning are not here. Setting off is the legs' business and the
        // arms carry on as they were; what does re-take the grip is the weapon coming
        // out of its walking carry, which is a different moment entirely and comes in
        // through TriggerShouldering when the item decides it. A lean moves the whole
        // body sideways without changing how anything is held.
        bool changed = isAiming != _wasAiming
            || isCrouching != _wasCrouching
            || isSprinting != _wasSprinting;

        _wasCrouching = isCrouching;
        _wasAiming = isAiming;
        _wasSprinting = isSprinting;

        if (changed)
            TriggerShouldering();

        _shoulderingPositionVelocity += (-shoulderingSpring * _shoulderingPositionOffset
            - shoulderingDamping * _shoulderingPositionVelocity) * Time.deltaTime;
        _shoulderingPositionOffset += _shoulderingPositionVelocity * Time.deltaTime;

        _shoulderingRotationVelocity += (-shoulderingSpring * _shoulderingRotationOffset
            - shoulderingDamping * _shoulderingRotationVelocity) * Time.deltaTime;
        _shoulderingRotationOffset += _shoulderingRotationVelocity * Time.deltaTime;
    }

    private void Awake()
    {
        _baseLocalPosition = transform.localPosition;
        _baseLocalRotation = transform.localRotation;
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

        float bobAmount = ForStance(bobIntensity);
        float swayAmount = ForStance(swayIntensity);
        float tiltStanceAmount = ForStance(tiltIntensity);
        float lookSwayAmount = ForStance(lookSwayIntensity);
        float lookTiltStanceAmount = ForStance(lookTiltIntensity);

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

        // Gated on the same isMoving the bob is, inverted -- so the two hand over to
        // each other on the same frame and never overlap. Vertical runs at half the
        // horizontal, which is what makes it a breath rather than a circle: the
        // chest rises once for every two small sways.
        float breathPhase = look.BreathPhase;

        Vector3 targetBreathOffset = isMoving
            ? Vector3.zero
            : new Vector3(
                Mathf.Sin(breathPhase) * breathHorizontalAmount,
                Mathf.Sin(breathPhase * 0.5f) * breathVerticalAmount,
                0f);

        Vector2 targetBreathRotation = isMoving
            ? Vector2.zero
            : new Vector2(
                Mathf.Sin(breathPhase * 0.5f) * breathPitchAmount,
                Mathf.Cos(breathPhase) * breathRollAmount);

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

        float targetLookTilt =
            -Mathf.Clamp(lookRate.x / lookTiltReferenceRate, -1f, 1f) * lookTiltAmount * lookTiltStanceAmount;

        // Nothing to lean into while a ladder or a car is driving the character:
        // the input is still being read there and would roll the hands over on a
        // key the player isn't steering with.
        float targetTilt = movement.IsMovementLocked
            ? 0f
            : -movement.MoveInput.x * tiltAmount * tiltStanceAmount;

        _currentTilt = Mathf.Lerp(_currentTilt, targetTilt, ForStance(tiltSmoothing) * Time.deltaTime);
        _currentBobOffset = Vector3.Lerp(_currentBobOffset, targetBobOffset, ForStance(bobSmoothing) * Time.deltaTime);
        _currentBobRotation = Vector3.Lerp(_currentBobRotation, targetBobRotation, ForStance(bobSmoothing) * Time.deltaTime);
        _currentSway = Vector3.Lerp(_currentSway, targetSway, ForStance(swaySmoothing) * Time.deltaTime);
        _currentLookSway = Vector3.Lerp(_currentLookSway, targetLookSway, ForStance(lookSwaySmoothing) * Time.deltaTime);
        _currentLookTilt = Mathf.Lerp(_currentLookTilt, targetLookTilt, ForStance(lookTiltSmoothing) * Time.deltaTime);
        _currentBreathOffset = Vector3.Lerp(_currentBreathOffset, targetBreathOffset, breathSmoothing * Time.deltaTime);
        _currentBreathRotation = Vector2.Lerp(_currentBreathRotation, targetBreathRotation, breathSmoothing * Time.deltaTime);

        UpdateShouldering();

        // Leaving the ground lifts the weapon and drops its muzzle; landing does the
        // reverse. Opposite signs on the two axes, so the pair reads as the weapon
        // being carried rather than as one rigid piece sliding up and down.
        if (movement.JumpedThisFrame)
        {
            _kickVelocity += jumpKickAmount;
            _kickPitchVelocity -= jumpKickPitchAmount;
            _kickRollVelocity -= jumpKickRollAmount;
        }

        if (movement.LandedThisFrame)
        {
            _kickVelocity -= landKickAmount;
            _kickPitchVelocity += landKickPitchAmount;
            _kickRollVelocity += landKickRollAmount;
        }

        _kickVelocity += (-kickSpring * _kickOffset - kickDamping * _kickVelocity) * Time.deltaTime;
        _kickOffset += _kickVelocity * Time.deltaTime;

        _kickPitchVelocity += (-kickSpring * _kickPitchOffset - kickDamping * _kickPitchVelocity) * Time.deltaTime;
        _kickPitchOffset += _kickPitchVelocity * Time.deltaTime;

        _kickRollVelocity += (-kickSpring * _kickRollOffset - kickDamping * _kickRollVelocity) * Time.deltaTime;
        _kickRollOffset += _kickRollVelocity * Time.deltaTime;

        // Straight off PeekAmount, which is already smoothed and already signed --
        // leaning left mirrors every axis without a second set of figures for it.
        Vector3 peek = peekRotation * look.PeekAmount;

        transform.localPosition = _baseLocalPosition
            + _currentBobOffset + _currentSway + _currentLookSway + _currentBreathOffset
            + Vector3.up * _kickOffset + _shoulderingPositionOffset;

        // Every rotational layer summed into one Euler off the base rather than
        // each writing the transform in turn. The old per-weapon version read
        // localEulerAngles back and overwrote one channel of it, which meant
        // whichever component wrote last that frame won -- composing them here
        // leaves nothing to win, and adding the next layer is one more term.
        //
        // Both tilts land on the same Z: one is the turn, the other the sidestep,
        // and a bank is a bank whichever asked for it.
        transform.localRotation = _baseLocalRotation * Quaternion.Euler(
            _currentBobRotation.x + _currentBreathRotation.x + peek.x + _kickPitchOffset + _shoulderingRotationOffset.x,
            _currentBobRotation.y + peek.y + _shoulderingRotationOffset.y,
            _currentBobRotation.z + _currentTilt + _currentLookTilt + _currentBreathRotation.y + peek.z + _kickRollOffset + _shoulderingRotationOffset.z);
    }
}

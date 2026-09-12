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

    [Header("Chest Follow")]
    // The socket on the upper chest bone. The hold follows where the animation puts
    // it, but only slowly -- a low-pass on the skeleton rather than a hard mount to
    // it. The same arrangement PlayerLook uses to follow the head, and for the same
    // reason: a shoulder rolling into a turn, a body leaning into a car, the drop of
    // a crouch are all already in the clips, and following the bone is how that
    // reaches the hands without anyone restating it in code. The clips also carry
    // per-frame stride jitter, which is what the damping is for -- the two live at
    // different frequencies, so one filter separates them.
    //
    // The upper chest rather than the hands themselves. What is wanted is where the
    // weapon is being carried from, and that is the torso the arms hang off; the hand
    // bones are already committed to gripping the model and following them would
    // simply restate the item's own animation.
    //
    // Empty leaves the hold exactly where the scene put it.
    [SerializeField] private Transform chestAnchor;

    // Where the hold sits relative to that socket is the scene's to decide: the
    // offset is measured once at startup from wherever the hold has been dragged to,
    // so it can be placed by eye in the viewport rather than typed in. Edit it in
    // Edit mode -- nudging it mid-play does nothing.
    //
    // 0 pins the hold to the camera rig and ignores the skeleton entirely; 1 follows
    // the chest in full. Between the two it follows part of the way, which is the
    // usual answer for a walk cycle with more shoulder in it than the hands want.
    [SerializeField, Range(0f, 1f)] private float chestFollowAmount = 1f;

    // Roughly how long the hold takes to catch up, in seconds. This is the whole
    // filter: too short and the stride comes through, too long and a crouch turns
    // into a slow sink. A few tenths is the usual band.
    [SerializeField] private float chestFollowSmoothTime = 0.35f;

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

    [Header("Bob Character")]
    // How much stronger one step is than the other. A real walk is not symmetric --
    // one leg leads, and the difference is small but constant.
    //
    // The sign picks which foot. It multiplies sin(bobPhase), which runs once per
    // stride against the footfall's twice, so it is positive through one step and
    // negative through the other -- and flipping it swaps which of them is the heavy
    // one. There is no way to read from here which foot the clip has down at that
    // moment, so choosing is a matter of looking at it and negating if it landed on
    // the wrong side.
    //
    // Worth knowing what it looks like: at 0.4 one step is 1.4x and the other 0.6x,
    // so the weapon rises more than twice as far on one foot as the other. That is a
    // limp, and deliberate, but it is easily mistaken for the bob being tied to one
    // leg. Around 0.1 it reads as a person rather than as an injury.
    //
    // Amplitude only, which is why it lives here rather than with the phase skew in
    // PlayerLook: how far the hands move is theirs to decide, while when they move is
    // shared with the camera and the legs.
    [SerializeField, Range(-0.5f, 0.5f)] private float bobStepAsymmetry = 0.4f;

    // A slow drift over the whole bob, so no two strides are quite the same size.
    // Small: this should be felt rather than seen, and past about 0.3 it stops
    // reading as a person walking and starts reading as one losing their footing.
    [SerializeField, Range(0f, 0.5f)] private float bobWander = 0.45f;

    // The rate this drifts at is PlayerLook's, alongside the phase and the cycle skew,
    // because it describes the stride rather than the hands. See BobWander there.

    [Header("Step / Jump / Land Shake")]
    // The hands taking the footfall. NOT the jump and landing coming back -- those were
    // deliberately given to the view alone, because leaving the ground and hitting it
    // again happen to the head while the hands go on holding something braced. A stride
    // is the opposite case: the hands are being CARRIED through it, and the impulse that
    // arrives twice a stride arrives at them too.
    //
    // It is also what the bob on its own cannot give. A sine has no events in it, so
    // however well it is tuned the hands oscillate rather than walk.
    //
    // Metres, downward. Positive dips the hands.
    [SerializeField] private float stepShakeAmount = 0.006f;


    // Degrees, and it ALTERNATES WITH THE FOOT -- the sign comes from PlayerLook's
    // footfall, so the hands and the head tip on the same foot instead of each deciding
    // for themselves which one it was.
    [SerializeField] private float stepShakeRollAmount = 0.6f;

    // LEAVING THE GROUND AND HITTING IT AGAIN, which the hands did not answer until now.
    //
    // The old arrangement gave both to the view alone, on the reasoning that they happen
    // to the head while the hands hold something braced. Half of that holds: the hands DO
    // stay braced, so they should not be thrown about. But something held has mass, and a
    // body that suddenly accelerates leaves it behind -- which is a small, sprung
    // displacement, not a throw. That is what these are.
    //
    // BOTH DIP, and that is not a mistake. On takeoff the body gains upward speed and
    // what it is carrying lags, so the weapon ends up lower in the hands. On landing the
    // fall is arrested and the weapon carries on down before it recovers. Two opposite
    // events, the same relative direction, because in both the body changes velocity
    // upward and the mass does not.
    //
    // Metres. Positive dips.
    [SerializeField] private float jumpShakeAmount = 0.004f;
    [SerializeField] private float landShakeAmount = 0.012f;

    // Degrees, and the same way every time rather than alternating like the footfall's.
    // That is the view's reasoning borrowed intact: a landing should be a thing the
    // player recognises, and a roll that picked a side each time would just be noise.
    [SerializeField] private float jumpShakeRollAmount = 0.3f;
    [SerializeField] private float landShakeRollAmount = 0.8f;

    // ONE SPRING FOR ALL THREE, which is what keeps them comparable: a footfall is a
    // small landing, and giving each its own spring would mean three settling times to
    // keep in step by hand. Matched to the view's own shake for the same reason.
    //
    // Critically-damped-ish: enough damping that a step does not ring, little enough
    // that it recovers rather than creeping back.
    [SerializeField] private float stepShakeSpring = 200f;
    [SerializeField] private float stepShakeDamping = 20f;

    [Header("Hand Tremor")]
    // Fear in the hands. Nothing drives this yet -- the slider is the whole interface for
    // now, and SetTremor below is what a scare, a wound or a held breath will push into
    // later.
    //
    // AT THE ITEM'S PIVOT, with the lean, the peek and the impulse shake -- computed here
    // and applied there, for the reason all four share.
    //
    // The hands grip the weapon, so a tremor in them turns it about the GRIP. The hold
    // origin is somewhere else, and rotating there swings the whole weapon through an arc
    // -- the muzzle travelling centimetres for a fraction of a degree, which reads as the
    // weapon being waved rather than as hands that cannot keep still.
    [Range(0f, 1f)]
    [SerializeField] private float tremor = 0f;

    // Metres and degrees at full strength. The rotation carries most of it: at arm's
    // length a fraction of a degree moves the muzzle further than a millimetre of shift
    // does, which is why a real tremor is read off the far end of what is held.
    [SerializeField] private float tremorPositionAmount = 0.0035f;
    [SerializeField] private float tremorRotationAmount = 0.5f;

    // Noise samples per second. A fear tremor sits somewhere around eight to twelve hertz
    // -- fast enough not to read as sway, slow enough not to read as a rendering fault.
    [SerializeField] private float tremorFrequency = 9f;

    // Seconds for the AMOUNT to catch up, not the tremor itself.
    //
    // The tremor needs no filtering -- Perlin noise is smooth by construction, and
    // filtering it would cost amplitude the same way filtering the bob's sine did. What
    // has to ease is the arrival: fear turns up all at once, and a shake that switches on
    // between two frames reads as a glitch rather than as a fright. Put here so every
    // future driver gets it and none has to remember to ramp its own value.
    [SerializeField] private float tremorSmoothTime = 0.25f;

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
    // Degrees, and a rotation rather than a slide. A weapon left behind by a turn
    // pivots in the hands -- the muzzle trails furthest and the stock barely moves --
    // where translating the whole thing sideways keeps it pointing where it always
    // did and reads as the weapon being on rails. The difference is most of what
    // separates a heavy weapon from a light one.
    //
    // Yaw answers horizontal mouse and pitch answers vertical; roll is the look tilt
    // below, which keeps its own rate and filter because a cant wants a lower ceiling
    // and a slower settle than a swing.
    [SerializeField] private float lookSwayYawAmount = 4f;
    [SerializeField] private float lookSwayPitchAmount = 3f;

    // Degrees per second that produces the full amount above. A brisk flick is
    // several hundred; anything past this is clamped, so a violent turn displaces
    // the hands no further than a firm one.
    [SerializeField] private float lookSwayReferenceRate = 240f;

    [SerializeField] private StanceValues lookSwayIntensity = new StanceValues(0.6f, 1f, 1.5f, 0.35f);

    // On top of that, one figure for each of the two things a turn can do to the
    // weapon -- because the sway is answering a different question in each.
    //
    // With free aim open the weapon already swings degrees behind the view on its
    // own, and a full sway on top is the same statement made twice: the two stack
    // into a weapon that slews around far more than any weight would explain.
    //
    // With it closed the weapon is locked to the view and a turn moves it not at all.
    // The sway is then the only thing saying the thing has mass, and it has to carry
    // the whole of that alone.
    //
    // A multiplier rather than a second set of amounts, so the shape stays authored
    // in one place and this only says how much of it survives.
    [SerializeField] private float lookSwayFreeAimOnMultiplier = 0.5f;
    [SerializeField] private float lookSwayFreeAimOffMultiplier = 1f;

    // Its own filter rather than sharing the movement sway's: raw mouse deltas are
    // spiky in a way a key press never is, and the two want different amounts of
    // smoothing to sit still.
    [SerializeField] private StanceValues lookSwaySmoothing = new StanceValues(16f, 12f, 9f, 18f);

    [Header("Peek")]
    // Degrees at a full lean, mirrored the other way for the other side. On top of
    // whatever the camera pivot's own peek roll already gives the weapon by carrying
    // it -- this is the hands doing something of their own, not a restatement of
    // that: bringing the weapon in tighter or turning it into the corner while the
    // view leans past it.
    //
    // Unsmoothed on purpose. PeekAmount is already eased into over peekSpeed, so
    // this follows a curve that has been shaped once rather than filtering a filter.
    //
    // Read by the held item and applied at its own pivot, not written here -- see
    // PeekRotation below for why.
    [SerializeField] private Vector3 peekRotation = new Vector3(0f, 0f, -6f);


    [Header("Look Tilt")]
    // Degrees of roll into the turn -- the same bank the strafe tilt gives, off the
    // mouse instead of the keys, and roll only like it is. Signed to match it, so
    // turning right and stepping right cant the hands the same way rather than
    // cancelling when both happen at once.
    //
    // Roll only, because yaw and pitch are the look sway's -- the three together are
    // one lag split across three axes, kept apart so each can be tuned without the
    // others.
    //
    // Not applied to this transform. Every other layer here rotates the whole hold
    // point, which swings whatever is held about the hands; a cant is the one thing
    // that should happen about the weapon's OWN axis instead, so a rifle rolls along
    // its barrel rather than describing an arc around the grip. The held item reads
    // this and applies it at its own pivot -- see Weapon.posDeltaPivot -- which means
    // nothing tilts at all with empty hands, which is correct.
    [SerializeField] private float lookTiltAmount = 5f;

    // The tilt's own rate and filter, deliberately not the sway's. A cant tends to
    // want a lower ceiling and a slower settle than a slide: the same flick that
    // should shove the hands right across should only tip them.
    [SerializeField] private float lookTiltReferenceRate = 200f;
    [SerializeField] private StanceValues lookTiltIntensity = new StanceValues(0.6f, 1f, 1.4f, 0.35f);
    [SerializeField] private StanceValues lookTiltSmoothing = new StanceValues(14f, 10f, 7f, 16f);

    // The cant's own pair, for the same reason as the sway's above and tuned apart
    // from it -- a turn that wants half the swing does not necessarily want half the
    // lean, and sharing one figure would decide that question by accident.
    [SerializeField] private float lookTiltFreeAimOnMultiplier = 0.5f;
    [SerializeField] private float lookTiltFreeAimOffMultiplier = 1f;

    private Vector3 _baseLocalPosition;
    private Quaternion _baseLocalRotation;
    private float _currentLookTilt;
    private Vector3 _currentBreathOffset;
    private Vector2 _currentBreathRotation;
    private Vector3 _chestReference;
    private bool _hasChestReference;
    private Vector3 _followedLocalPosition;
    private Vector3 _followVelocity;
    private Vector3 _currentBobOffset;
    private Vector3 _currentBobRotation;
    private Vector3 _currentSway;
    private float _stepShakeOffset;
    private float _stepShakeVelocity;
    private float _stepShakeRoll;
    private float _stepShakeRollVelocity;
    private float _currentTremor;
    private float _tremorVelocity;
    private Vector3 _tremorOffset;
    private Vector3 _tremorRotation;
    private Vector2 _currentLookSway;
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
    // Seconds the character has been walking without anything taking the hands away
    // from it.
    //
    // Here rather than on the weapon because it describes the character, not what it
    // happens to be holding. Kept on the weapon it reset on every swap, so an item
    // drawn mid-stride had to earn the walking carry again from nothing -- arriving
    // high and sinking into a pose the item it replaced was already in. Counted here,
    // a swap does not touch it and the new item simply reads how long the walk has
    // been going.
    //
    // Not reset by standing still, which is what makes the carry stick: somebody who
    // stops walking does not present their weapon. Only InterruptWalkPose clears it,
    // and only the held item knows when that applies.
    public float WalkTime { get; private set; }

    // How hard the hands are shaking, zero to one. Pushed in by whatever decides the
    // character is frightened -- a scare, a wound, a held breath -- the same
    // push-values-in pattern everything else here uses.
    //
    // Writes the serialized field rather than shadowing it, so the Inspector slider and
    // this are the same number. Which means the slider works for authoring right up until
    // something starts pushing, and then stops -- the honest behaviour, and the reason not
    // to keep a separate runtime value that would silently win.
    public void SetTremor(float amount) => tremor = Mathf.Clamp01(amount);

    // A tremor is noise, not a wave, and that is the whole of why it reads as nerves
    // rather than as machinery. A sine at nine hertz is a vibration; noise at nine hertz
    // never repeats.
    //
    // SIX SEPARATE NOISE LINES, three for the offset and three for the rotation, each
    // read at its own place in the field. Sharing one line across the axes would move them
    // in lockstep, which is a single rigid wobble in one direction -- and sharing between
    // the offset and the rotation would weld the shift to the tilt, so the hands would
    // swing about a fixed point instead of shaking.
    private void UpdateTremor()
    {
        // The amount eases; the noise does not. See tremorSmoothTime.
        _currentTremor = Mathf.SmoothDamp(
            _currentTremor, Mathf.Clamp01(tremor), ref _tremorVelocity, tremorSmoothTime);

        if (_currentTremor <= 0.0001f)
        {
            _tremorOffset = Vector3.zero;
            _tremorRotation = Vector3.zero;

            return;
        }

        float t = Time.time * tremorFrequency;

        _tremorOffset = TremorNoise(t, 11.3f, 41.7f, 73.1f)
            * (tremorPositionAmount * _currentTremor);

        _tremorRotation = TremorNoise(t, 127.9f, 191.5f, 233.7f)
            * (tremorRotationAmount * _currentTremor);
    }

    // Perlin returns zero to one, so it is recentred to plus and minus one -- left as it
    // comes, the hands would be displaced to one side and tremble about that instead of
    // about where the pose put them.
    private static Vector3 TremorNoise(float t, float lineX, float lineY, float lineZ)
    {
        return new Vector3(
            Mathf.PerlinNoise(t, lineX) - 0.5f,
            Mathf.PerlinNoise(t, lineY) - 0.5f,
            Mathf.PerlinNoise(t, lineZ) - 0.5f) * 2f;
    }

    // Called by whatever is held when something needs the hands elsewhere -- a shot,
    // the sights, a reload. Clearing it means the walking carry has to be walked into
    // again afterwards rather than snapping back the moment the interruption ends.
    public void InterruptWalkPose() => WalkTime = 0f;

    // Degrees of cant from the look, for whatever is currently held to apply at its
    // own pivot. Computed here because the figures, the stance table and the filter
    // all belong with the rest of the hand motion -- only the point it turns about
    // belongs to the item.
    //
    // Read rather than pushed, so an item that has no opinion about canting simply
    // never asks, and nothing here has to know which items those are.
    public float LookTilt => _currentLookTilt;

    // Degrees of lean from the peek, on the same terms and for the same reason as
    // LookTilt: computed here, where the figures live, and turned about the item's
    // own pivot rather than about the hold point.
    //
    // Which pivot it turns about is the whole of the difference. Rotating the hold
    // swings the item through an arc around the grip, so a lean reads as the weapon
    // being waved sideways; rotating at the item's pivot turns it in place, which is
    // what leaning into a corner actually looks like. The two are the same numbers
    // and completely different motions.
    public Vector3 PeekRotation => look != null ? peekRotation * look.PeekAmount : Vector3.zero;

    // Every impulse the hands take -- footfalls, jumps, landings -- as one displacement
    // and one roll, for whatever is held to apply at its own pivot.
    //
    // The same division as the look tilt and the peek above, and for the same reason.
    // Rolling the hold point swings the whole weapon around the grip in an arc; rolling
    // at the item turns it in place, which is what a weapon rocking in the hands looks
    // like. The figures, the spring and the stance scaling stay here with the rest of the
    // hand motion -- only the point it turns about belongs to the item.
    //
    // One pair rather than a pair per cause, because the hands are one thing and take one
    // displacement. A landing mid-stride adds to the step it interrupted.
    //
    // The dip is published as a vector so the item can simply add it, and it ends up in
    // the item's own space rather than the world's: the hands drop along the weapon's
    // axis, not along global up.
    public Vector3 ImpulseShake => Vector3.up * _stepShakeOffset;

    public float ImpulseShakeRoll => _stepShakeRoll;

    // The tremor, for the item to apply at its own pivot alongside the three above. All
    // three Euler axes rather than a single roll, because a tremor has no favoured
    // direction -- that is most of what separates it from a lean.
    public Vector3 Tremor => _tremorOffset;

    public Vector3 TremorRotation => _tremorRotation;

    // The chest socket in whatever space the hold's localPosition is written in.
    // Taken from the hold's actual parent rather than assuming anything about the
    // rig, so it can be nested a level deeper without this quietly reading the wrong
    // space and throwing the hands off into the world.
    private Vector3 ChestLocalPosition
    {
        get
        {
            Transform holdSpace = transform.parent != null ? transform.parent : transform;
            return holdSpace.InverseTransformPoint(chestAnchor.position);
        }
    }

    private void Awake()
    {
        _baseLocalPosition = transform.localPosition;
        _baseLocalRotation = transform.localRotation;
        _followedLocalPosition = _baseLocalPosition;
    }

    // The one job here: catch where the chest starts, once.
    //
    // It has to be here rather than in Awake because the Animator poses the skeleton
    // between Update and LateUpdate, so this is the first moment the bone is where
    // the animation actually puts it. Taken in Awake the reference would be the bind
    // pose, and the gap between that and the idle pose would be read as movement the
    // chest had already made -- dragging the hold off the position it was placed at
    // the instant play began.
    private void LateUpdate()
    {
        if (!_hasChestReference && chestAnchor != null)
        {
            _chestReference = ChestLocalPosition;
            _hasChestReference = true;
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

        float bobAmount = ForStance(bobIntensity);
        float swayAmount = ForStance(swayIntensity);

        // The depth of field used to take a blur magnitude from here, measured off this
        // bob. It does not any more, and the reason is worth keeping: this figure reports
        // MOVEMENT, and movement is the wrong question. A weapon held ready while the legs
        // are going is still a weapon held ready, and softening it blurs the thing the
        // player is looking at. What earns the blur is the item being dropped into its
        // walk or run offset -- which only the item can see, so the item pushes it.
        float tiltStanceAmount = ForStance(tiltIntensity);
        // Folded into the stance figure rather than applied further down, so it lands
        // on the TARGET and not on the filtered value. The smoothing then carries the
        // change across for free -- toggling free aim eases the sway to its new size
        // over lookSwaySmoothing instead of stepping it, and there is no second
        // filter here doing what the existing one already does.
        bool freeAim = look.IsFreeAimActive;

        float lookSwayAmount = ForStance(lookSwayIntensity)
            * (freeAim ? lookSwayFreeAimOnMultiplier : lookSwayFreeAimOffMultiplier);
        float lookTiltStanceAmount = ForStance(lookTiltIntensity)
            * (freeAim ? lookTiltFreeAimOnMultiplier : lookTiltFreeAimOffMultiplier);

        // Sprinting is not walking, so it stops the count rather than adding to it --
        // and the interruption is left to the held item, which is the only thing that
        // knows about firing and reloading.
        if (isMoving && !movement.IsSprintingStable)
            WalkTime += Time.deltaTime;

        float bobPhase = look.BobPhase;

        // Two things stop every stride being a copy of the last one.
        //
        // The first is that left and right are not the same step. sin(bobPhase) runs
        // once per stride while the footfall runs twice, so it is positive through
        // one step and negative through the other -- which makes it exactly the
        // signal for telling the two apart, at no cost.
        float stepBias = 1f + Mathf.Sin(bobPhase) * bobStepAsymmetry;

        // The second is a slow wander with no period at all, so strides vary across
        // several steps.
        //
        // TAKEN FROM PlayerLook RATHER THAN GENERATED HERE. It used to have a noise
        // source of its own, and two sources meant the head could be growing its stride
        // while the hands shrank theirs -- the same walk, described two different ways.
        // The gait lengthening and shortening is something the whole body does at once,
        // which puts it with the phase and the skew: shared signal, local amount. The
        // amount below is still the hands' own.
        float wander = 1f + look.BobWander * bobWander;

        bobAmount *= stepBias * wander;

        // The footfall, taken from PlayerLook rather than found again here: the phase
        // lives there, so the crossing is found there, and a second search of the same
        // signal would be a second opinion about when the foot lands.
        //
        // Scaled by the same figure the bob itself is, which is what ties the jolt to
        // the step that produced it: the heavier foot of a limp lands harder, a
        // sprint stamps, a crouch barely touches down, and the wander keeps
        // consecutive steps from thumping identically. All of that comes free from
        // multiplying by a number that already carries it.
        if (look.FootfallThisFrame)
        {
            _stepShakeVelocity -= stepShakeAmount * bobAmount;
            _stepShakeRollVelocity += stepShakeRollAmount * bobAmount * look.FootfallSign;
        }

        // Into the SAME accumulators as the footfall, not springs of their own. The hands
        // are one thing and they take one displacement; a jump landed on mid-stride
        // should add to the step it interrupted rather than be tracked beside it.
        //
        // Not scaled by bobAmount either -- that figure describes a stride, and a landing
        // is not one. It lands at full strength whatever the gait was.
        if (movement != null)
        {
            if (movement.JumpedThisFrame)
            {
                _stepShakeVelocity -= jumpShakeAmount;
                _stepShakeRollVelocity += jumpShakeRollAmount;
            }

            // The landings that count, at full strength. A flight of stairs lands once per
            // tread and none of them should jolt the weapon -- PlayerMovement decides
            // which ones are worth reacting to, from the speed it arrested.
            if (movement.LandedHardThisFrame)
            {
                _stepShakeVelocity -= landShakeAmount;
                _stepShakeRollVelocity += landShakeRollAmount;
            }
        }

        // Integrated every frame whether a step landed or not -- a spring that is only
        // advanced when it is struck never comes back.
        _stepShakeVelocity +=
            (-stepShakeSpring * _stepShakeOffset - stepShakeDamping * _stepShakeVelocity) * Time.deltaTime;
        _stepShakeOffset += _stepShakeVelocity * Time.deltaTime;

        _stepShakeRollVelocity +=
            (-stepShakeSpring * _stepShakeRoll - stepShakeDamping * _stepShakeRollVelocity) * Time.deltaTime;
        _stepShakeRoll += _stepShakeRollVelocity * Time.deltaTime;

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
        // told apart: the flick that has already swung the weapon as far as it goes
        // can still have room left to lean.
        //
        // X is pitch and Y is yaw, matching the order they are summed into the
        // rotation below. Negated, so the weapon is left behind by the turn instead
        // of leading it -- which is the whole point, and the sign to flip if it ever
        // looks like the hands are steering.
        Vector2 targetLookSway = new Vector2(
            -Mathf.Clamp(lookRate.y / lookSwayReferenceRate, -1f, 1f) * lookSwayPitchAmount * lookSwayAmount,
            -Mathf.Clamp(lookRate.x / lookSwayReferenceRate, -1f, 1f) * lookSwayYawAmount * lookSwayAmount);

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
        _currentLookSway = Vector2.Lerp(_currentLookSway, targetLookSway, ForStance(lookSwaySmoothing) * Time.deltaTime);

        _currentLookTilt = Mathf.Lerp(_currentLookTilt, targetLookTilt, ForStance(lookTiltSmoothing) * Time.deltaTime);
        _currentBreathOffset = Vector3.Lerp(_currentBreathOffset, targetBreathOffset, breathSmoothing * Time.deltaTime);
        _currentBreathRotation = Vector2.Lerp(_currentBreathRotation, targetBreathRotation, breathSmoothing * Time.deltaTime);

        // Critically damped rather than lerped: a spring that never overshoots, so a
        // crouch settles onto its new height instead of dipping past it and coming
        // back. smoothTime is then an honest "how long to catch up" rather than a
        // rate whose meaning changes with the distance.
        //
        // One frame behind the bone, because the Animator poses the skeleton after
        // Update. That is exactly what PlayerLook's head follow does, and at a
        // smoothTime measured in tenths of a second the lag is far below what the
        // filter is already removing.
        //
        // Position only. The head follow takes nothing but position either, and for a
        // sharper reason here: the hold's rotation is the aim, and letting the chest
        // turn it would put the skeleton in an argument with where the player is
        // pointing.
        //
        // The scene's position plus however far the chest has MOVED since play began
        // -- not the chest's position with an offset bolted on. The two are the same
        // arithmetic and a completely different result: where the hold was dragged to
        // is kept exactly, and the skeleton only ever contributes change.
        Vector3 followTarget = _hasChestReference
            ? _baseLocalPosition + (ChestLocalPosition - _chestReference)
            : _baseLocalPosition;

        _followedLocalPosition = Vector3.SmoothDamp(
            _followedLocalPosition, followTarget, ref _followVelocity, chestFollowSmoothTime);

        // Rebuilt from the scene's base every frame rather than nudged from where it
        // was left, so nothing can accumulate an offset here over time.
        //
        // No peek slide here. The hold is shared by everything that gets picked up,
        // and how far a thing has to be pulled in to clear a corner is a fact about
        // that thing -- so it lives on the item, beside its other poses.
        UpdateTremor();

        transform.localPosition =
            Vector3.Lerp(_baseLocalPosition, _followedLocalPosition, chestFollowAmount)
            + _currentBobOffset + _currentSway + _currentBreathOffset;

        // Every rotational layer summed into one Euler off the base rather than
        // each writing the transform in turn. The old per-weapon version read
        // localEulerAngles back and overwrote one channel of it, which meant
        // whichever component wrote last that frame won -- composing them here
        // leaves nothing to win, and adding the next layer is one more term.
        //
        // Both tilts land on the same Z: one is the turn, the other the sidestep,
        // and a bank is a bank whichever asked for it.
        //
        // The peek is not here. It, like the look tilt, is turned about the item's
        // own pivot instead -- both are read off this component by whatever is held.
        //
        // Neither is the impulse shake -- the footfalls, the jump and the landing. Those
        // join the tilt and the peek in being worked out here and applied by the item, at
        // its own pivot.
        //
        // The view still gets its own, larger version of the jump and landing on the
        // rendered camera, which the items are deliberately not parented to. The hands
        // answering as well is not that decision reversed: the view is thrown, where the
        // hands only lag. Something held has mass and a body that changes speed leaves it
        // behind, which is a small sprung displacement rather than a shake.
        transform.localRotation = _baseLocalRotation * Quaternion.Euler(
            _currentBobRotation.x + _currentLookSway.x + look.FreeAim.x + _currentBreathRotation.x,
            _currentBobRotation.y + _currentLookSway.y + look.FreeAim.y,
            _currentBobRotation.z + _currentTilt + _currentBreathRotation.y);
    }
}

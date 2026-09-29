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

    // THE CHEST FOLLOW IS GONE, and the reason is worth keeping: it was the second of
    // two filters on one signal.
    //
    // It trailed the upper chest bone through its own smoothed filter so the skeleton's
    // breathing, its shoulder roll and the drop of a crouch reached whatever was being
    // held. All of that still arrives -- the hold hangs off the camera pivot and the
    // pivot follows the skeleton itself -- so this was re-filtering a signal that had
    // already been filtered once, at a different smoothTime, one level up. Two
    // first-order filters in series are not twice the filtering; they are a phase
    // relationship nobody picked, and what it produced was a weapon reaching its stride
    // peak on a different frame than the view did. A weapon swimming against the head
    // rather than riding it.
    //
    // PlayerLook's follow is now the only one, and it tracks the NECK rather than the
    // head socket. See neckAnchor there.

    [Header("Bob")]
    // Metres and degrees at a walk. Roll carries the most of it: a swinging arm
    // rocks a weapon side to side far more than it lifts or turns it, and leaning
    // on that one axis buys weight without the sights wandering off the middle.
    [SerializeField] private float bobHorizontalAmount = 0.025f;
    [SerializeField] private float bobVerticalAmount = 0.035f;
    [SerializeField] private float bobPitchAmount = 3f;
    [SerializeField] private float bobYawAmount = 4f;
    [SerializeField] private float bobRollAmount = 6f;

    [Header("Shouldering")]
    // The weapon being re-settled against the shoulder. Fired whenever the way the
    // character is carrying itself changes: dropping into a crouch or standing back up,
    // raising or lowering the sights, breaking into a run or falling out of one -- and by
    // the held item when it comes out of its walking carry.
    //
    // Also at the END of a draw and the END of a reload, both halves, when the clip has
    // stopped moving the weapon and the hands actually have it. During either it would be a
    // second opinion about where the weapon is, which is why an earlier attempt at firing
    // only the view's half on those two read as the camera twitching for no visible cause.
    // The end of them is not that moment: nothing is animating any more.
    //
    // Not on an item swap. A swap has no single instant to answer -- the stow and the draw
    // are two clips with the hands empty between them -- and the draw's own landing above
    // already covers the half that arrives.
    //
    // One jolt at a time: a trigger arriving before the last has settled is refused rather
    // than restarting it. See TriggerShouldering.
    //
    // One motion for all of them, not a value per event. They are the same physical thing
    // -- a grip adjusting to a body that has just moved under it -- and giving each its own
    // would only be the same settle authored several times, drifting apart as they were
    // tuned.
    //
    // ── TWO PHASES, BECAUSE THAT IS WHAT THE GESTURE IS ──────────────────────────────
    //
    // The weapon comes OFF the shoulder and is then seated back into it better than it was.
    // Away, then in. That is the motion being described, and it is the reason a spring was
    // never going to produce it: an impulse into a spring is one displacement decaying, so
    // the return is whatever the damping leaves and the settle is a consequence rather than
    // a beat. There is no number on a spring that means "and then press it home".
    //
    // These are that shape written out. Every figure is a distance, an angle or a duration,
    // so what is on the screen is what happens.
    //
    //   1. AWAY   -- over shoulderingAwayTime, the weapon travels to shoulderingAwayOffset
    //                and shoulderingAwayRotation. This is the lift off the shoulder.
    //   2. SEAT   -- over shoulderingSeatTime it comes back, pressing PAST home by
    //                shoulderingSeatOvershoot of the away amount halfway through, then
    //                landing on it. That press is the weapon being set in properly.
    //
    // TIMED TO SPAN THE AIM TRANSITION, which is what it is for. The jolt fires when the
    // sights start coming up or going down, and the two together are one action: the weapon
    // is being re-seated because the pose is moving. The two times should add up to about
    // however long the weapon's own aim transition takes -- shorter and the settle finishes
    // while the pose is still travelling, longer and it is still fidgeting after the sights
    // have arrived.
    [SerializeField] private Vector3 shoulderingAwayOffset = new Vector3(0f, -0.01f, 0.03f);
    [SerializeField] private Vector3 shoulderingAwayRotation = new Vector3(5f, 0f, -2f);
    [SerializeField] private float shoulderingAwayTime = 0.12f;
    [SerializeField] private float shoulderingSeatTime = 0.22f;

    // How far past home the seat presses, as a fraction of the away amount. 0 is a plain
    // return; this is the beat that makes it read as being set in rather than let go of.
    [SerializeField, Range(0f, 1f)] private float shoulderingSeatOvershoot = 0.4f;

    // A SPRING CHASING THE TWO PHASES RATHER THAN REPLACING THEM.
    //
    // The phases above are what the weapon is being ASKED to do; this is how a heavy thing
    // in two hands actually follows an instruction. On their own the phases are exact -- the
    // weapon is wherever the timings say on the frame they say, which is why an authored
    // gesture can read as animation rather than as mass. A spring on its own has the mass
    // and no opinion about the gesture. The phases as a target and a spring as the follower
    // gives both: the shape is still the one written above, and it is arrived at with
    // weight, a little lag, and whatever overshoot the ratio allows on top of the seat's own.
    //
    // Frequency and damping RATIO rather than stiffness and drag, the same pair the look
    // sway tilt uses: ratio is how bouncy, frequency is how quick, and neither disturbs the
    // other. As stiffness and drag, changing the speed changes the bounciness the wrong way
    // round.
    //
    // ZERO FREQUENCY MEANS NO SPRING -- the offsets are the phases, to the frame. A real
    // setting rather than an off switch: a gesture timed against an aim transition wants to
    // land when it says it lands, and a spring can only blur that.
    //
    // IT DOES NOT LENGTHEN THE LOCKOUT, deliberately. The spring's tail outlives the phases,
    // and a new gesture starting during it is not a problem here the way restarting an
    // impulse would be: this spring chases a target, so it simply starts chasing the new one
    // from wherever it had got to. That is a handover rather than a cancellation, and it is
    // what keeps back-to-back stance changes from being refused.
    [SerializeField] private float shoulderingFollowFrequency = 26f;

    [SerializeField, Range(0f, 2f)] private float shoulderingFollowDamping = 0.8f;

    // Read off this component and applied by whatever is held, at ITS own pivot -- see
    // ShoulderingOffset. The hold does not move.

    // The view's share, FIRED WHEN THE ITEM'S IS FINISHED rather than alongside it.
    //
    // Which is a sequence, not a coincidence, and it is the sequence the motion has: the
    // weapon is lifted and set in, and the head registers it once it lands. Fired together
    // the two read as one shove with a camera wobble on it; fired after, the view answers
    // the weapon -- the thing has been seated and the body felt it arrive.
    //
    // Still an impulse rather than a distance, unlike the item's phases above. PlayerLook
    // strikes its own spring with whatever arrives, and a settle IS what the head does here,
    // so there is nothing to author -- the figures are smaller than they look, though:
    // divided by the square root of the spring, 0.1 is about seven millimetres.
    //
    // Tuned HERE rather than in PlayerLook. One motion, one
    // place to set it: split across two components the halves would be adjusted separately
    // and end up disagreeing about a thing that only ever happens once.
    // PlayerLook.AddShoulderingKick takes the spring as well as the impulse for exactly
    // that reason.
    //
    // Far smaller figures than the hands'. A stance change moves a weapon held out at
    // arm's length several centimetres; it moves the head a few millimetres, and matching
    // the two reads as the camera being shoved rather than the body resettling under it.
    [SerializeField] private Vector3 cameraShoulderingPosition = new Vector3(0f, -0.1f, 0.08f);
    [SerializeField] private Vector3 cameraShoulderingRotation = new Vector3(-12f, 0f, 4f);
    [SerializeField] private float cameraShoulderingSpring = 220f;
    [SerializeField] private float cameraShoulderingDamping = 22f;

    [Header("Bob (Sprint)")]
    // The same five again, for a run, and REPLACING the ones above rather than scaling
    // them.
    //
    // bobIntensity already has a sprint figure, and one figure is all it can ever be: a
    // sprint could only be the walk's shape drawn larger or smaller. Which is the same
    // objection StanceValues was written to answer, one level down -- a run is not a walk
    // at a different size. The arm travels further and swings from a different place, so
    // what wants to be mostly vertical at a walk can want to be mostly lateral at a run,
    // and no single multiplier gets from one to the other.
    //
    // Five numbers say it directly. The cadence is not among them: that comes from the
    // animator's own phase and is one decision for the whole player, so a run's bob is
    // faster because the run is, not because anything here says so.
    //
    // There was briefly a mirror here instead -- one axis chosen, the other two exchanged
    // while sprinting -- which got a vertical swing to read as lateral without a second
    // set of figures. It is gone: exchanging axes can only ever produce the walk's own
    // amplitudes in a different order, so the run was still the walk, just rotated.
    [SerializeField] private float sprintBobHorizontalAmount = 0.05f;
    [SerializeField] private float sprintBobVerticalAmount = 0.02f;
    [SerializeField] private float sprintBobPitchAmount = 2f;
    [SerializeField] private float sprintBobYawAmount = 7f;
    [SerializeField] private float sprintBobRollAmount = 9f;

    // How long the changeover takes, in seconds. It cannot be instant: the two sets are
    // different motions, not one motion at two sizes, so switching between them on the
    // frame the sprint key goes down snaps the weapon across. Eased, the two cross over,
    // and every point in between is a swing in its own right because each channel is
    // being interpolated on the same phase.
    //
    // Its own figure rather than bobSmoothing's, which is the envelope's -- that one
    // decides how quickly the bob starts and stops, and this decides how quickly it
    // changes shape. Sharing one meant a gait change that read well made a standing start
    // read badly.
    [SerializeField] private float sprintBobBlendTime = 0.25f;

    // ── ONE THING TO KNOW BEFORE TUNING THESE ───────────────────────────────────────
    //
    // bobIntensity's SPRINT entry still multiplies everything below, because the envelope
    // is shared by all four stances and crouch and aim have no amount set of their own to
    // replace it with. So while a run's shape now lives here, its overall size is decided
    // in two places, and that is one quantity with two owners -- which is the thing this
    // file's other comments keep warning about.
    //
    // Set Bob Intensity -> Sprint to the same value as Walk and the figures above are the
    // run, exactly as written. Left different, every one of them is being scaled by a
    // number somewhere else, and tuning one will feel like the other is fighting it.

    // THERE WAS A SHARPNESS KNOB HERE AND IT WAS THE WRONG OPERATOR. It raised each
    // channel to a power -- |s|^k -- to gather the motion into the footfall, and the
    // result trembled.
    //
    // The reason is geometric rather than a bug. Yaw and roll are sin and cos of the same
    // phase, so the pair traces a CIRCLE; raising both to a power turns that circle into a
    // superellipse, and above 1 the superellipse is concave. A concave path reverses
    // direction sharply where it crosses the axes, four times a stride, which is exactly
    // what a tremble is. The offset pair has the same problem in a figure-eight.
    //
    // A wave whose axes describe one shape cannot be sharpened one axis at a time. What
    // can be changed without distorting the shape is WHEN the point moves around it --
    // a reparametrisation, not a reshaping -- which is PlayerLook.bobCycleSkew, and it is
    // shared with the camera and the legs because timing is shared.
    //
    // For the hit at the footfall itself, the step shake below is the honest instrument:
    // an impulse is an event, and a hard step is an event rather than a louder wave.

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

    [Header("Look Sway Tilt")]
    // The same turn, told a second time -- at the item's own pivot, and on a spring.
    //
    // TWO THINGS SEPARATE IT FROM THE LOOK SWAY ABOVE, and it needs both.
    //
    // The pivot: the sway rotates the HOLD, so the weapon swings around the grip through
    // an arc and the whole thing displaces sideways as it turns. This turns it in place,
    // about the point the hands are gripping, so the muzzle leads and the stock trails.
    // Both are true of a real turn and neither reads right alone -- one is the arms
    // carrying it across, the other is the weapon pivoting in them.
    //
    // The filter: the sway is lerped and this is sprung, so they arrive at different
    // times and with different shapes. The quick part is the hands reacting; the slow,
    // overshooting part is what they are holding catching up.
    //
    // NOT ROLL. The look tilt below is the roll, and it already has this turn's cant
    // covered at the same pivot. This is pitch and yaw only.
    [SerializeField] private float lookSwayTiltYawAmount = 3f;
    [SerializeField] private float lookSwayTiltPitchAmount = 2f;

    // A SPRING RATHER THAN A LERP, which is the difference between arriving and settling.
    //
    // A lerp only ever approaches: fastest at the start, slowing all the way in, never
    // passing the target. However slow it is made, the weapon just gets there late. A
    // spring carries momentum, so it runs past the turn and comes back -- and that
    // overshoot is what reads as weight, because a thing with mass does not stop where
    // the hands stopped.
    //
    // Everything that settles in this project is a spring already: the fire kick, the step
    // shake, the jump and landing. Those all pull towards zero; this one is driven, so it
    // pulls towards a target that is itself moving.
    //
    // FREQUENCY AND DAMPING RATIO, NOT SPRING AND DAMPING. The raw pair looks simpler and
    // is a trap here: the damping ratio is c / 2*sqrt(k), so holding c fixed while the
    // stance changes k changes how bouncy it is per stance -- and it changes it the wrong
    // way round, with aiming (the stiffest) coming out the loosest. Expressed like this,
    // the ratio means the same thing in every stance and only the speed moves.
    //
    // Frequency in radians per second: higher settles sooner. Ratio at 1 is critical --
    // no overshoot at all, which is a lerp with better manners; below that it starts to
    // bounce, and 0.4 to 0.6 is where a carried weapon lives.
    [SerializeField] private StanceValues lookSwayTiltFrequency = new StanceValues(9f, 6f, 4.5f, 11f);

    [Range(0.1f, 1f)]
    [SerializeField] private float lookSwayTiltDamping = 0.5f;



    // THERE IS NO SHARED PEEK ANGLE HERE ANY MORE. It was a cant in degrees, mirrored per
    // side, applied by whatever was held at its own pivot -- one figure tuned on the
    // character and reaching every item.
    //
    // It is gone because it made a weapon's own peek figure the second of two. An item has
    // its own offset and its own poses, all of them facts about that model's build and
    // parenting, and a lean is no different: how far a barrel has to come round to clear a
    // corner is a fact about its length. With both in play, tuning the weapon's meant
    // working against a number in another component, and there was no set of three degrees
    // that meant the same thing on a pistol and a rifle anyway.
    //
    // Weapon.peekRotation is the whole of it now -- magnitude on X and Y, signed on Z,
    // because the cant is the one axis a lean has a side on.

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
    private Vector3 _shoulderingPositionOffset;
    private Vector3 _shoulderingRotationOffset;

    // The follow spring's state -- how far behind the phases the weapon currently is, not
    // anything about the gesture itself. The gesture is the clock below.
    private Vector3 _shoulderingPositionVelocity;
    private Vector3 _shoulderingRotationVelocity;

    // Seconds into the gesture, and the clock both halves are sequenced off: the item's
    // phases are read from it, and the view's kick is fired when it crosses the end.
    //
    // Parked past the end at startup so nothing plays on the first frame, when every edge
    // flag is still at its default and all three would read as having just changed.
    private float _shoulderingTime = 999f;
    private bool _cameraShoulderingPending;

    // Set by anyone asking for a gesture and consumed at one point in the frame, so the
    // asker's execution order cannot change where the gesture starts. See
    // TriggerShouldering.
    private bool _shoulderingRequested;


    // Edge trackers for the states the gesture fires on. See UpdateShouldering.
    private bool _wasCrouching;
    private bool _wasAiming;
    private bool _wasSprinting;
    private bool _wasPeeking;

    // 0 walking, 1 sprinting -- how far through the changeover between the two bob sets.
    private float _sprintBobBlend;
    private Vector3 _currentBobOffset;
    private Vector3 _currentBobRotation;

    // 0 standing, 1 walking. The bob's envelope -- see where it is applied for why the
    // smoothing lives here rather than on the oscillation.
    private float _bobWeight;
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
    private Vector2 _currentLookSwayTilt;
    private Vector2 _lookSwayTiltVelocity;

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

    // How long the weapon's two phases take together. The end of this is also when the
    // view's kick fires, so it is the one figure the whole sequence is hung off.
    private float ShoulderingItemTime => Mathf.Max(shoulderingAwayTime, 0f) + Mathf.Max(shoulderingSeatTime, 0f);

    // And how long before another may start: THE WEAPON'S PHASES, and nothing else.
    //
    // This used to add the view's settle on top, on the reasoning that the view has not even
    // begun when the weapon finishes so the whole sequence is not over until it has. Which
    // is true and was the wrong rule: the lockout came out at twice the length of anything
    // visible, so a second stance change a third of a second after the weapon had plainly
    // finished did nothing at all. That is the "sometimes it does not trigger" -- not a
    // missed edge, a refused one.
    //
    // The view's spring being restarted mid-settle is not a problem worth a lockout for. It
    // is a spring struck again, which is what every other spring in this rig does and reads
    // as a second event rather than as a glitch.
    private float ShoulderingLockout => ShoulderingItemTime;

    // ASKS FOR A GESTURE, RATHER THAN STARTING ONE. The request is honoured in
    // UpdateShouldering, at one fixed point in the frame.
    //
    // Starting it here was the other half of the trouble, and it had nothing to do with the
    // motion. Two places ask for this -- the stance edges below, and the held item when it
    // leaves its walking carry -- and the item runs at a different execution order, so it
    // was setting the clock to zero BEFORE this component's own update, which then advanced
    // it by a frame before the phases were read. So the same gesture started at zero when
    // the stance asked for it and a frame in when the weapon did, and which of the two won
    // depended on who happened to ask first for a change that fired both. That is the
    // "sometimes it is faster": not a different duration, a different starting point.
    //
    // As a request it cannot happen: whoever asks, the gesture begins where
    // UpdateShouldering begins it, and the phases are read from exactly zero on that frame.
    //
    // REFUSED WHILE ONE IS STILL RUNNING, and a refusal is DROPPED rather than queued --
    // restarting mid-gesture would read as the first being cancelled, and honouring it later
    // would jolt for a stance change the player has already finished making.
    public void TriggerShouldering() => _shoulderingRequested = true;

    // Edges, not states. What is wanted is the moment the character changes how it is
    // carrying itself, and a flag that is simply true for a while has two of those in it.
    //
    // Raising or lowering the sights, dropping into a crouch or coming back up, breaking
    // into a run or falling out of one, leaning out past a corner or coming back. Crouching
    // counts down the sights as much as off them -- the whole body drops half a metre either
    // way, and a weapon held against a shoulder that has just moved is exactly what this is
    // for.
    //
    // Leaning was left out once, on the grounds that a peek moves the whole body sideways
    // without changing how anything is held. That is true of the body and not of the arms:
    // the weapon is pulled in to clear the corner and pushed back out afterwards, so the grip
    // really is re-taken twice.
    //
    // Walking is still not here. Setting off is the legs' business and the arms carry on as
    // they were; what does re-take the grip is the weapon coming out of its walking carry,
    // which is a different moment entirely and comes in through TriggerShouldering when the
    // item decides it -- along with the frame a draw lands and the end of a reload, both for
    // the same reason: the clip has stopped moving the weapon and the hands now have it.
    private void UpdateShouldering()
    {
        // Advanced before the trigger is considered, so a gesture starting this frame is
        // read at exactly zero rather than one frame in. Clamped so the clock cannot run
        // away over a long session and lose precision against the lockout.
        _shoulderingTime = Mathf.Min(_shoulderingTime + Time.deltaTime, 1000f);

        bool isCrouching = movement.IsCrouching;
        bool isAiming = movement.IsAiming;
        bool isSprinting = movement.IsSprintingStable;

        // THE COMMAND, NOT THE ANGLE. PeekAmount is the lean actually in force and is eased,
        // so it only crosses back under a threshold once the body has finished unwinding --
        // a fifth of a second after the key came up, by which time the gesture is answering
        // nothing the player did. PeekCommand moves on the frame the key does.
        //
        // Still a threshold rather than a comparison with zero, because the axis is a float
        // and a stick can rest just off centre.
        bool isPeeking = Mathf.Abs(look.PeekCommand) > 0.01f;

        // EMPTY HANDS HAVE NOTHING TO RE-SEAT, so nothing in this block runs. Every figure in
        // it describes a weapon being pulled off a shoulder and set back into it, and with
        // nothing held the gesture is describing an object that is not there -- while the
        // view's half would go on kicking for crouches and leans taken bare-handed, which is
        // a camera twitching for no cause on screen.
        //
        // BEFORE the pending kick below, not after, so holstering mid-gesture cancels the
        // answer rather than letting it land on an empty pair of hands a moment later.
        //
        // The edges are consumed here rather than skipped, so a stance change made with empty
        // hands cannot be sitting there waiting to fire the instant something is drawn. What
        // the draw itself deserves it gets from the draw -- see Weapon's own trigger.
        if (look.HandsFree)
        {
            _wasAiming = isAiming;
            _wasCrouching = isCrouching;
            _wasSprinting = isSprinting;
            _wasPeeking = isPeeking;

            _shoulderingRequested = false;
            _cameraShoulderingPending = false;

            _shoulderingPositionOffset = Vector3.zero;
            _shoulderingRotationOffset = Vector3.zero;
            _shoulderingPositionVelocity = Vector3.zero;
            _shoulderingRotationVelocity = Vector3.zero;

            return;
        }

        // THE VIEW'S HALF, ON THE FRAME THE WEAPON'S FINISHES -- and checked here, before a
        // new gesture can be started below, not after.
        //
        // After, a request arriving on exactly the frame the weapon lands resets the clock
        // first and this test then reads the NEW gesture's time, which is zero: the finished
        // gesture's kick is swallowed and only goes off at the end of the chain. Checked
        // first, every gesture that completes gets its answer on the frame it completes,
        // however soon the next one begins.
        if (_cameraShoulderingPending && _shoulderingTime >= ShoulderingItemTime)
        {
            _cameraShoulderingPending = false;

            look.AddShoulderingKick(
                cameraShoulderingPosition,
                cameraShoulderingRotation,
                cameraShoulderingSpring,
                cameraShoulderingDamping);
        }

        bool changed = isAiming != _wasAiming
            || isCrouching != _wasCrouching
            || isSprinting != _wasSprinting
            || isPeeking != _wasPeeking;

        // Consumed whether or not a gesture results, which is what makes a refusal a drop
        // rather than a delay. Left unconsumed the flag would still differ next frame and
        // the gesture would go off the instant the lockout expired.
        _wasAiming = isAiming;
        _wasCrouching = isCrouching;
        _wasSprinting = isSprinting;
        _wasPeeking = isPeeking;

        if (changed)
            _shoulderingRequested = true;

        // Spent whether or not it is honoured, for the same reason the edges are. Every
        // gesture in the game starts on this line, so none of them can start anywhere else.
        if (_shoulderingRequested)
        {
            _shoulderingRequested = false;

            if (_shoulderingTime >= ShoulderingLockout)
            {
                _shoulderingTime = 0f;
                _cameraShoulderingPending = true;
            }
        }

        // AWAY, THEN SEAT. One scalar drives both the offset and the rotation, so the twist
        // and the travel are one gesture -- authored apart they would be two motions that
        // happen to overlap, and nothing keeps them from drifting out of step as either is
        // tuned.
        //
        // Smoothstepped within each phase rather than linear: a lift that starts and stops
        // instantly is a slide, and the ease is most of what gives the shape its shoulders
        // before the follow spring below adds any weight of its own.
        float away = Mathf.Max(shoulderingAwayTime, 0f);
        float seat = Mathf.Max(shoulderingSeatTime, 0f);
        float amount;

        if (_shoulderingTime >= away + seat || away + seat <= 0f)
        {
            amount = 0f;
        }
        else if (_shoulderingTime < away)
        {
            // Out of the shoulder, reaching the full away figure exactly as the phase ends.
            amount = away > 0f ? Mathf.SmoothStep(0f, 1f, _shoulderingTime / away) : 1f;
        }
        else
        {
            float s = seat > 0f ? (_shoulderingTime - away) / seat : 1f;

            // Back in, and PAST home on the way -- the sine is zero at both ends and peaks
            // in the middle, so the press happens during the return and lands on nothing
            // rather than leaving the weapon parked past its pose.
            amount = Mathf.SmoothStep(1f, 0f, s)
                - Mathf.Sin(s * Mathf.PI) * shoulderingSeatOvershoot;
        }

        // Where the phases say to be, which is a target rather than the answer.
        Vector3 targetPosition = shoulderingAwayOffset * amount;
        Vector3 targetRotation = shoulderingAwayRotation * amount;

        // No spring asked for: the phases ARE the motion, to the frame.
        if (shoulderingFollowFrequency <= 0f)
        {
            _shoulderingPositionOffset = targetPosition;
            _shoulderingRotationOffset = targetRotation;
            _shoulderingPositionVelocity = Vector3.zero;
            _shoulderingRotationVelocity = Vector3.zero;

            return;
        }

        // Rebuilt from the frequency and the ratio each frame rather than stored, the same
        // way the look sway tilt does it -- two multiplies, and nothing to keep in step with
        // the fields.
        float omega = shoulderingFollowFrequency;
        float stiffness = omega * omega;
        float drag = 2f * shoulderingFollowDamping * omega;

        _shoulderingPositionVelocity += ((targetPosition - _shoulderingPositionOffset) * stiffness
            - _shoulderingPositionVelocity * drag) * Time.deltaTime;
        _shoulderingPositionOffset += _shoulderingPositionVelocity * Time.deltaTime;

        _shoulderingRotationVelocity += ((targetRotation - _shoulderingRotationOffset) * stiffness
            - _shoulderingRotationVelocity * drag) * Time.deltaTime;
        _shoulderingRotationOffset += _shoulderingRotationVelocity * Time.deltaTime;
    }

    // The shouldering jolt, for the item to apply AT ITS OWN PIVOT -- worked out here and
    // deliberately not applied here, the same arrangement the peek, the look tilt, the
    // tremor and the impulse shake are in.
    //
    // A re-settle is the weapon being pulled back against the shoulder and twisting in the
    // hands as it goes. Both of those are about the weapon, and the hold point is not on
    // the weapon: rotating there swings the whole thing through an arc around the grip,
    // which reads as the gun being waved rather than shouldered, and translating there
    // moves the hold and everything hanging off it. At the item's pivot it draws back down
    // its own line and turns in place, which is what the motion is.
    //
    // The view's half stays on the rendered camera, where the item is not parented -- so
    // the two halves cannot end up applying to the same transform.
    public Vector3 ShoulderingOffset => _shoulderingPositionOffset;

    public Vector3 ShoulderingRotation => _shoulderingRotationOffset;

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

    // NO EDGE DETECTION, which is most of why this is simpler than the curve it replaced.
    //
    // The target is just "where should the weapon be right now", and the spring is always
    // chasing it. Raising the sights moves the target and the spring follows; dropping
    // them moves it back and the spring follows again. Interrupting halfway needs no
    // special case at all -- the spring is already partway there with the velocity it had,
    // and carrying that velocity into the new move is exactly what a hand does.
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

    // The bob's yaw and roll, for the item to apply at its own pivot. Pitch is not here --
    // it stays on the hold.
    //
    // WHICH IS A SPLIT DOWN THE MIDDLE OF ONE MOTION, and it is deliberate. The vertical
    // dip runs at twice the rate and is the arms carrying the weapon up and down, so it
    // belongs where the arms are. The yaw and roll run at stride rate and are the weapon
    // rocking in the grip as the body rolls from foot to foot -- and rocking about the
    // hold point swings the whole thing sideways through an arc instead, which is the
    // thing the pivot exists to avoid.
    public Vector3 BobYawRoll => new Vector3(0f, _currentBobRotation.y, _currentBobRotation.z);

    // The turn's lag, in pitch and yaw, for the item to apply at its own pivot.
    //
    // X is pitch and Y is yaw, matching the order they are summed into a rotation -- and
    // the order the hold's own look sway uses, so the two layers cannot end up describing
    // the same turn on different axes.
    //
    // Roll is not here: that is LookTilt, which already carries this turn's cant. A third
    // opinion about the same roll would just be the cant applied twice.
    public Vector2 LookSwayTilt => _currentLookSwayTilt;

    // The tremor, for the item to apply at its own pivot alongside the three above. All
    // three Euler axes rather than a single roll, because a tremor has no favoured
    // direction -- that is most of what separates it from a lean.
    public Vector3 Tremor => _tremorOffset;

    public Vector3 TremorRotation => _tremorRotation;

    private void Awake()
    {
        _baseLocalPosition = transform.localPosition;
        _baseLocalRotation = transform.localRotation;
    }

    // No LateUpdate any more. It existed for one job -- catching where the chest bone
    // started, once, after the Animator had posed the skeleton -- and with the follow
    // gone there is nothing here that needs to run after the pose.

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
        // THE ENVELOPE IS SMOOTHED, NOT THE OSCILLATION, and this is what puts the punch
        // back into the footfall.
        //
        // These two used to be lerped straight into _currentBobOffset and
        // _currentBobRotation, which low-passes the sine itself. A first-order filter cuts
        // a sine's amplitude by k/sqrt(k^2+w^2) and delays it by atan(w/k), and both get
        // worse as the frequency rises -- so the doubled-rate channels, the vertical and
        // the pitch, lost the most. Those are exactly the ones that peak at the footfall,
        // which is why the step read as soft however the amounts were tuned: the filter
        // was rounding off the peaks and nothing else.
        //
        // It also moved with cadence, so a sprint lost more than a walk while bobIntensity
        // was trying to make the sprint bigger. Same fault the camera's bob had, same fix:
        // the phase comes from the animator and is smooth by construction, so the sine
        // needs no filtering. What has to ease is starting and stopping, and that is a
        // scalar.
        // THE STANCE FIGURE IS IN THE ENVELOPE, NOT BESIDE IT, and that is what stops a
        // sprint arriving as a step.
        //
        // ForStance switches hard -- walk to sprint is one figure replaced by another
        // between two frames -- and the lerp that used to absorb it was the one on the
        // oscillation, which had to go. Left outside, the amplitude jumped the moment the
        // key went down. Folded in here it eases across instead, and the filter is doing
        // the job it was always described as doing: smoothing an amplitude, not a wave.
        //
        // So this one scalar now carries both things that can change abruptly: whether the
        // character is moving at all, and which gait it is moving in.
        float targetBobWeight = isMoving ? ForStance(bobIntensity) : 0f;

        // Exponential rather than rate-times-delta: the old form is a different time
        // constant at every framerate, and past k*dt = 1 it overshoots outright.
        _bobWeight = Mathf.Lerp(
            _bobWeight, targetBobWeight, 1f - Mathf.Exp(-ForStance(bobSmoothing) * Time.deltaTime));

        // The per-step variation stays OUTSIDE the filter. Smoothing the step bias and the
        // wander would average away exactly the difference between one stride and the next
        // that they exist to create.
        float bobScale = _bobWeight * stepBias * wander;

        // THE AMOUNTS ARE BLENDED, NOT THE FINISHED WAVES, and it is the same distinction
        // the envelope is built on. Each channel keeps its own phase -- the vertical and
        // the pitch run at double rate, the rest at single -- so interpolating the figures
        // and then building one wave gives a single coherent swing at every point in the
        // changeover. Building two waves and crossfading them would be two swings at once
        // wherever the blend is not at an end, and two sines of the same frequency summed
        // out of amplitude produce a beat rather than a stride.
        _sprintBobBlend = sprintBobBlendTime > 0f
            ? Mathf.MoveTowards(
                _sprintBobBlend,
                movement.IsSprintingStable ? 1f : 0f,
                Time.deltaTime / sprintBobBlendTime)
            : (movement.IsSprintingStable ? 1f : 0f);

        float horizontalAmount = Mathf.Lerp(bobHorizontalAmount, sprintBobHorizontalAmount, _sprintBobBlend);
        float verticalAmount = Mathf.Lerp(bobVerticalAmount, sprintBobVerticalAmount, _sprintBobBlend);
        float pitchAmount = Mathf.Lerp(bobPitchAmount, sprintBobPitchAmount, _sprintBobBlend);
        float yawAmount = Mathf.Lerp(bobYawAmount, sprintBobYawAmount, _sprintBobBlend);
        float rollAmount = Mathf.Lerp(bobRollAmount, sprintBobRollAmount, _sprintBobBlend);

        Vector3 targetBobOffset = new Vector3(
            Mathf.Cos(bobPhase) * horizontalAmount,
            Mathf.Sin(bobPhase * 2f) * verticalAmount,
            0f) * bobScale;

        Vector3 targetBobRotation = new Vector3(
            Mathf.Sin(bobPhase * 2f) * pitchAmount,
            Mathf.Sin(bobPhase) * yawAmount,
            Mathf.Cos(bobPhase) * rollAmount) * bobScale;


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

        // The pivot layer's target, off the same normalised rate and the same stance
        // figure. Only the amounts and the filter differ -- reading the rate twice would
        // be two answers to "how fast is the view turning", and the reference rate is
        // already the one place that question is settled.
        Vector2 targetLookSwayTilt = new Vector2(
            -Mathf.Clamp(lookRate.y / lookSwayReferenceRate, -1f, 1f) * lookSwayTiltPitchAmount * lookSwayAmount,
            -Mathf.Clamp(lookRate.x / lookSwayReferenceRate, -1f, 1f) * lookSwayTiltYawAmount * lookSwayAmount);

        float targetLookTilt =
            -Mathf.Clamp(lookRate.x / lookTiltReferenceRate, -1f, 1f) * lookTiltAmount * lookTiltStanceAmount;

        // Nothing to lean into while a ladder or a car is driving the character:
        // the input is still being read there and would roll the hands over on a
        // key the player isn't steering with.
        float targetTilt = movement.IsMovementLocked
            ? 0f
            : -movement.MoveInput.x * tiltAmount * tiltStanceAmount;

        _currentTilt = Mathf.Lerp(_currentTilt, targetTilt, ForStance(tiltSmoothing) * Time.deltaTime);
        // Assigned, not filtered. The easing these two used to get is in the weight the
        // targets were built with -- see the envelope above.
        _currentBobOffset = targetBobOffset;
        _currentBobRotation = targetBobRotation;
        _currentSway = Vector3.Lerp(_currentSway, targetSway, ForStance(swaySmoothing) * Time.deltaTime);
        _currentLookSway = Vector2.Lerp(_currentLookSway, targetLookSway, ForStance(lookSwaySmoothing) * Time.deltaTime);

        // Driven damped spring, integrated the same way every other spring here is: a
        // pull towards the target proportional to how far away it is, minus a drag
        // proportional to how fast it is already going.
        //
        // k and c are rebuilt from the frequency and the ratio each frame rather than
        // stored, so a stance change moves the speed and leaves the bounce alone -- see
        // the fields. The arithmetic is two multiplies and is not worth caching.
        float tiltOmega = ForStance(lookSwayTiltFrequency);
        float tiltStiffness = tiltOmega * tiltOmega;
        float tiltDrag = 2f * lookSwayTiltDamping * tiltOmega;

        Vector2 tiltAcceleration =
            (targetLookSwayTilt - _currentLookSwayTilt) * tiltStiffness
            - _lookSwayTiltVelocity * tiltDrag;

        _lookSwayTiltVelocity += tiltAcceleration * Time.deltaTime;
        _currentLookSwayTilt += _lookSwayTiltVelocity * Time.deltaTime;

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
        // THERE IS NO CHEST FOLLOW HERE ANY MORE. The hold sits at the position the scene
        // placed it and moves only by the offsets below.
        //
        // It used to trail the upper chest bone through its own smoothed filter, so the
        // skeleton's breathing and stride reached whatever was being held. That was a
        // second follow doing the same job as PlayerLook's, one level down -- the hold
        // hangs off the camera pivot, so the pivot's own follow was already carrying the
        // skeleton into it, and this one added its own lag and its own smoothing on top.
        // Two filters in series on one signal is not twice the filtering, it is a phase
        // relationship nobody chose: the hold reached a stride peak at a different moment
        // than the view did, which is a weapon that swims against the head rather than
        // riding it.
        //
        // One follow now, on the pivot, and it follows the NECK -- see PlayerLook's. The
        // neck is the joint the head turns on, so a pivot that tracks it is tracking the
        // base of the view rather than the view itself, and everything under the pivot,
        // this hold included, is carried by it for free.
        //
        // Rebuilt from the scene's base every frame rather than nudged from where it was
        // left, so nothing can accumulate an offset here over time.
        //
        // No peek slide here. The hold is shared by everything that gets picked up,
        // and how far a thing has to be pulled in to clear a corner is a fact about
        // that thing -- so it lives on the item, beside its other poses.
        UpdateTremor();
        UpdateShouldering();

        transform.localPosition = _baseLocalPosition
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
            _currentLookSway.y + look.FreeAim.y,
            _currentTilt + _currentBreathRotation.y);
    }
}

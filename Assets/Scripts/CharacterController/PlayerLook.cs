using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerLook : MonoBehaviour
{
    // One figure per stance. Deliberately the same shape as HandMotion's, because
    // the view and the hands answer the same four situations and describing them
    // differently in the two places would make a matched pair impossible to tune.
    //
    // Not shared as a type: it is a handful of floats, and the coupling a shared
    // definition would create between the camera and the item rig costs more than
    // the duplication saves.
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

    [Header("Input")]
    [SerializeField] private InputActionAsset inputActions;

    [Header("Look")]
    // The one thing that decides where the player is looking from. A plain child
    // of the capsule, driven entirely from here -- never parented to a bone and
    // never following one.
    //
    // It used to hard-lock to a socket on the head bone, which is what put every
    // frame of spine and neck animation straight into the view. Worse, the aim
    // rig's target hangs off this transform, so the rig twisted the spine, the
    // spine moved the head, the head moved the camera, and the camera moved the
    // target the rig was aiming at -- a closed loop with nothing damping it.
    // Cutting the camera off the skeleton is what breaks that loop.
    [SerializeField] private Transform cameraPivot;
    [SerializeField] private float mouseSensitivity = 0.1f;

    // How far the view can pitch on foot, in degrees, split four ways: up and down
    // are separate because they are not the same movement -- a neck extends further
    // looking down at your own feet than back over your own brow -- and standing and
    // crouched are separate because a crouch has already spent part of that range
    // getting the head down there.
    //
    // Only on foot. A ladder and a car have their own limits below, for reasons that
    // have nothing to do with what a neck can do: one is a body facing a wall, the
    // other a head inside a cabin.
    [SerializeField] private float standingPitchUpLimit = 85f;
    [SerializeField] private float standingPitchDownLimit = 85f;
    [SerializeField] private float crouchPitchUpLimit = 85f;
    [SerializeField] private float crouchPitchDownLimit = 85f;

    // And with nothing in the hands, which replaces the pair above rather than
    // scaling it -- empty hands are their own case, not a discount on a stance.
    //
    // The reason to separate them is that the limits above are really the weapon's.
    // What stops the view going further is that the arms and the barrel have to come
    // with it and there is nowhere left for them to go; drop the weapon and that
    // reason is gone, so the head is free to crane much further than a rifle would
    // let it. This is also the case the head mount is on for, which is the other half
    // of the same thought: empty hands are when the camera is most nearly a head.
    //
    // One pair rather than a standing and a crouched one: with nothing held there is
    // far less to separate them, and a second pair would be two more numbers to tune
    // for a difference nobody looks for.
    [SerializeField] private float noItemPitchUpLimit = 85f;
    [SerializeField] private float noItemPitchDownLimit = 85f;

    [Header("Head Follow")]
    // The socket on the head bone. The view follows where the animation puts it,
    // but only slowly: a low-pass on the skeleton rather than a hard mount to it.
    //
    // Crouching, climbing, leaning into a car -- all of it is already in the
    // clips, and following the bone is how that reaches the view without anyone
    // restating it in code. What the clips also carry is per-frame stride jitter,
    // and that is what the damping is for. Both live at different frequencies, so
    // one filter separates them: slow gestures pass, shake does not.
    [SerializeField] private Transform headAnchor;

    // Where the eyes sit relative to that socket is the scene's to decide: the
    // pivot's offset from the head is measured once at startup from wherever it
    // has been dragged to, so it can be placed by eye in the viewport rather than
    // typed in. Edit it in Edit mode -- nudging it mid-play does nothing.
    //
    // 0 pins the view to the capsule and ignores the skeleton entirely; 1 follows
    // the head in full. Between the two it follows part of the way, which is the
    // usual answer for a walk cycle with more shoulder in it than the view wants.
    [SerializeField, Range(0f, 1f)] private float headFollowAmount = 1f;

    // Roughly how long the view takes to catch up with the head, in seconds. This is
    // the whole filter: too short and the stride comes through, too long and the
    // skeleton stops reaching the view at all. A few tenths is the usual band.
    [SerializeField] private float headFollowSmoothTime = 0.35f;

    // An extra drop while crouched, on top of whatever the crouch clip already
    // gives, in metres. Still applied on its own rather than folded into the
    // follow target, so it lands in full even at a headFollowAmount of 0 -- the
    // two are separate systems and turning one off should not take the other with
    // it. Leave at 0 if the clip's own drop is enough.
    [SerializeField] private float crouchEyeDrop = 0.2f;

    // How long that drop takes, in seconds. Its own figure rather than the follow's,
    // which it used to borrow -- they are filtering different things and want
    // different answers. The follow is a low-pass on stride jitter and is tuned by
    // how much of the walk cycle it lets through; this is a deliberate movement with
    // a start and an end, and is tuned by how quickly a crouch should feel like it
    // has happened. Sharing one number meant a view steady enough to walk with could
    // only sink into a crouch, and a crouch that snapped brought the stride with it.
    [SerializeField] private float crouchDropTime = 0.15f;

    // Empty hands, a ladder, a car: three states with nothing to hold steady, and in
    // all three the view sits ON the head socket instead of following it.
    //
    // The follow above exists for the weapon. Filtering the skeleton is what keeps a
    // stride out of a held barrel, and the price of it is that the view is never
    // quite where the head is -- which is invisible while something is in frame to
    // anchor it, and is exactly the thing that makes empty hands feel like a floating
    // camera. On a ladder or in a car the animation is the whole performance and the
    // view has no business smoothing it.
    //
    // The items themselves are unaffected either way: they hang off the pivot and are
    // carried wherever it goes, and by definition there is nothing there to carry in
    // any of the three states this covers.
    [SerializeField] private bool headMountWhenHandsAreFree = true;

    // How long the changeover takes, in seconds. It cannot be instant: the mount and
    // the follow are the authored eye offset apart -- the very gap the reference
    // measures -- so drawing a weapon would snap the view by that much.
    [SerializeField] private float headMountBlendTime = 0.25f;

    // Whose hands they are. Only read to ask whether they are empty; left unassigned
    // the mount falls back to the ladder and the car, which need no help to answer.
    [SerializeField] private PlayerItems items;

    [Header("Peek")]
    // A sideways slide, in metres. Nothing bends and nothing rolls: the view and
    // everything under it -- the weapon, the aim target -- step out to the side as
    // one rigid piece and step back.
    //
    // Deliberately not a lean. A lean has to be built twice, once as a spine bend
    // and once as camera geometry, and two constructions of the same motion can be
    // made to look alike but never to agree: the gap between them is a weapon
    // hanging off the camera while the shoulders go somewhere else, and that gap
    // is the hands coming off the grips. A slide has nothing to disagree with.
    [SerializeField] private float peekDistance = 0.35f;

    // Degrees of roll on top of that slide. One value covers the view and the
    // weapon both, because the weapon hangs off the pivot -- rolling the pivot
    // rolls the pair as one rigid piece, and there is no second construction of
    // it to drift out of step. The body stays level under them, which is the
    // trade for that: a small angle reads as the head cocking with the lean, a
    // large one as shoulders that forgot to come along.
    [SerializeField] private float peekTilt = 6f;

    // How much of the slide is covered per second: 5 reaches full in a fifth of a
    // second. Constant-rate rather than damped so leaning out and back takes the
    // same time either way, which is what makes it something a player can count on
    // under fire.
    [SerializeField] private float peekSpeed = 5f;

    // How much of the lean survives at the pitch limit, as a fraction: 1 leaves it
    // untouched at any angle, 0 closes it completely looking straight up or down.
    // Scaled linearly from level, the same shape the car uses to trade neck twist
    // against neck tilt.
    //
    // The reason is the same too. A lean is a body weight shift, and the further the
    // head is already committed -- craned back, or looking at its own boots -- the
    // less of one is left. Held out sideways AND pitched to the limit is a pose
    // nothing can hold, and on screen it reads as the camera having come loose from
    // the character rather than as a person peeking.
    //
    // Applied to the lean itself rather than to any one thing that reads it, so the
    // slide, the roll, the hands and the torso bend all give way together.
    [SerializeField, Range(0f, 1f)] private float peekAtMaxPitchRatio = 0.35f;

    [Header("Strafe Tilt")]
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private float tiltAmount = 3f;
    [SerializeField] private float tiltSpeed = 8f;

    [Header("Aim")]
    // The camera that actually draws the frame -- the one carrying the Brain, not
    // the vcam and not the pivot. Optional: left empty it finds Camera.main, so
    // this can't be the thing that silently isn't wired.
    [SerializeField] private Camera renderCamera;

    [Header("Speed FOV")]
    [SerializeField] private CinemachineCamera cinemachineCamera;
    [SerializeField] private float maxFovBoost = 10f;
    [SerializeField] private float fovSpeed = 8f;


    [Header("Ladder Look")]
    [SerializeField] private float climbLookYawLimit = 100f;
    [SerializeField] private float climbLookPitchUpLimit = 60f;
    [SerializeField] private float climbLookPitchDownLimit = 60f;

    [Header("Car Look")]
    [SerializeField] private float carLookYawLimitRight = 60f;
    [SerializeField] private float carLookYawLimitLeft = 60f;
    [SerializeField] private float carLookPitchUpLimit = 30f;
    [SerializeField] private float carLookPitchDownLimit = 30f;
    [SerializeField, Range(0f, 1f)] private float carPitchLimitAtMaxYawRatio = 0.2f;

    [Header("Camera Breathing")]
    [SerializeField] private float breathFrequency = 1f;
    [SerializeField] private float breathPitchAmount = 0.3f;
    [SerializeField] private float breathYawAmount = 0.3f;
    [SerializeField] private float breathRollAmount = 0.3f;
    [SerializeField] private float breathSmoothing = 4f;

    [Header("Camera Bob")]
    // The bob's cadence comes from the legs, through PlayerAnimator's own read of
    // the locomotion clip, rather than from a frequency set here. A number tuned to
    // roughly match the walk cycle is the one thing that can never work: it is
    // right at exactly one speed and drifts in and out of phase everywhere else,
    // and the view rocking against the footfall instead of with it is worse than no
    // bob at all.
    //
    // Left empty the bob falls back to its own clock at bobFallbackFrequency, which
    // is the old behaviour and is only there so this component still does something
    // on its own.
    [SerializeField] private PlayerAnimator playerAnimator;

    // Degrees of head start, for lining the bob's low point up with the footfall.
    //
    // The phase arrives as the walk clip's normalised time, which says where in the
    // clip playback is but not where in the clip a foot actually touches down --
    // that depends entirely on how the animation was authored and there is no
    // reading it from here. So the bob starts out landing at some arbitrary point in
    // the stride, and this slides it until the dip and the step coincide. 360 is a
    // whole stride, 180 swaps which foot it agrees with.
    //
    // Dialled in by eye once, per locomotion set. Wrong, it is worse than no bob at
    // all: the view rocks against the footfall instead of with it.
    //
    // 135 is a reasoned starting point rather than a measured one. The vertical bob
    // is sin(phase * 2), so it bottoms out a hundred and thirty-five degrees into
    // the cycle -- and looping walk clips are conventionally authored to start on a
    // foot contact. If this one does, that puts the dip on the step. If it does not,
    // the offset is wrong by however far the clip's contact sits from its start, and
    // the fix is to watch the feet and slide this until they agree. Adding or
    // subtracting 180 swaps which foot it agrees with.
    [SerializeField] private float bobPhaseOffset = 135f;

    // How unevenly the walk cycle runs. Zero is a plain sine, where every part of a
    // step takes the same time as every other -- correct as maths and wrong as a
    // person, since the drop onto a foot is quicker than the push back off it.
    //
    // Shared by everything that reads BobPhase, so raising it makes the view, the
    // hands and the weapon all breathe the same unevenness rather than one of them
    // developing a limp the others do not have.
    //
    // 0.3 is where it is felt without being seen -- the walk stops being metronomic
    // and does not yet look like a limp. Past about 0.6 it reads as a stagger, which
    // is a character choice rather than a default.
    [SerializeField, Range(0f, 0.9f)] private float bobCycleSkew = 0.3f;

    // The bob's own clock, used ONLY when playerAnimator above is empty. With an
    // animator the cadence is the walk clip's and this is never read.
    //
    // Radians per second, not cycles -- it accumulates straight into the phase. A
    // stride is one full turn, so 6 is very close to one stride a second, which at
    // two footfalls each is about the cadence of an ordinary walk. That is why it is
    // already the right number and not a placeholder.
    [SerializeField] private float bobFallbackFrequency = 6f;

    [SerializeField] private float bobPitchAmount = 0.5f;
    [SerializeField] private float bobYawAmount = 0.5f;
    [SerializeField] private float bobRollAmount = 0.5f;

    // Scales all three, per stance, the same way HandMotion scales the hands.
    //
    // It replaces the gait speed ratio the amounts used to be multiplied by. That
    // ratio is one dial: a crouch could only ever be a scaled-down walk and a sprint
    // the same walk scaled up. A crouch wants small and tight, a sprint wants wide
    // and loose, and aiming wants the view close to still regardless of which of the
    // two it is happening in -- none of which is one curve read at three points.
    //
    // The ratio still sets the FALLBACK CLOCK's rate above, which is a cadence rather
    // than an amount and does belong to speed.
    [SerializeField] private StanceValues bobIntensity = new StanceValues(0.35f, 1f, 2f, 0.2f);

    [Header("Free Aim")]
    // Whether the window is in use at all. Toggled in play by the FreeAimToggle
    // action (U), and this is the value it starts from.
    //
    // Switched off it closes the way aiming closes it -- handing what it holds to the
    // camera rather than swinging the weapon back to the middle -- so the crosshair
    // stays on whatever it was on across the toggle. A switch that moved the aim
    // would be a switch nobody could use in a fight.
    [SerializeField] private bool freeAimEnabled = true;

    // A window the weapon swings in, on top of a view that turns normally.
    //
    // The two move at once: the mouse turns the camera by its full amount, exactly as
    // it would with none of this, and the weapon swings ahead of centre in the same
    // direction and settles back once the mouse stops. Nothing is taken from the
    // view -- an earlier version had the window absorb input before the camera saw
    // it, which did make the weapon move first, but it also meant the mouse did
    // nothing at all until the window filled. That is a deadzone, and it reads as the
    // camera being slow.
    //
    // It lives here rather than with the hands because the offset is measured against
    // what the view actually turned by, and this is where that is known.
    //
    // Degrees either side of centre. Small: past ten or so the weapon spends its time
    // visibly off the middle of the screen and the player stops being able to guess
    // where a shot will go.
    [SerializeField] private float freeAimYawLimit = 6f;
    [SerializeField] private float freeAimPitchLimit = 4f;

    // How far the weapon swings per degree the view turns. 1 is a degree for a
    // degree; below that the weapon only creeps toward the edge of its window and
    // takes a long turn to get there, above it the window fills almost at once.
    //
    // The limits above decide how far it can go; this decides how quickly a turn
    // takes it there, which is a separate question and the one that actually decides
    // whether the weapon feels heavy or loose.
    [SerializeField] private float freeAimAmount = 1f;

    // How quickly the weapon returns to centre when the window closes.
    //
    // Only for that -- there is no idle drift back. Left alone the weapon stays
    // wherever the last turn put it inside the window, which is the point: an offset
    // that quietly recentred itself would be moving the muzzle without the player
    // asking, and turning the other way is what brings it back.
    [SerializeField] private float freeAimCentreSpeed = 14f;

    [Header("Camera Look Tilt")]
    // Degrees of roll into a turn, off how fast the view is actually turning rather
    // than how far the mouse moved -- so it is the same at any frame rate and stops
    // dead the moment the view reaches a yaw limit.
    //
    // Far smaller than the hands' version of this, and on the rendered camera rather
    // than the pivot. The pivot carries the items, and HandMotion already leans them
    // for the same turn: putting this there too would tilt the weapon twice for one
    // mouse movement. Here it rolls the view past a weapon that is doing its own,
    // lesser lean, which is the relationship the two should have.
    [SerializeField] private float lookTiltAmount = 1.5f;

    // Degrees per second that produces the full amount. A brisk flick is several
    // hundred; past this it is clamped, so a violent turn cants the view no further
    // than a firm one.
    [SerializeField] private float lookTiltReferenceRate = 200f;

    [SerializeField] private StanceValues lookTiltIntensity = new StanceValues(0.6f, 1f, 1.4f, 0.35f);

    // Its own filter rather than the bob's: raw mouse deltas are spiky in a way a
    // footfall never is, and the two need different amounts of smoothing to sit
    // still.
    [SerializeField] private float lookTiltSmoothing = 10f;

    // Amounts above are at a full walk; the character's actual speed scales them.
    // Not the rate any more -- the clip decides that, and a run clip is already
    // faster than a walk one without anyone multiplying anything.
    //
    // Clamped because the ratio has no ceiling of its own: a vehicle or a future
    // gait could hand it anything.
    [SerializeField] private float maxBobSpeedRatio = 2f;
    [SerializeField] private float bobSmoothing = 8f;

    [Header("Camera Jump / Land Shake")]
    // The view's alone -- none of this reaches the weapon. Leaving the ground and
    // hitting it again are things that happen to the head; the hands are holding
    // something braced and go on holding it. It lands on the rendered camera rather
    // than the pivot, which is the one transform under the rig the items do not
    // hang off, so there is nothing to take back out afterwards.
    //
    // Pitch is the drop and the recovery; roll is the head not landing perfectly
    // square, and rolls the same way every time so a landing is a thing the player
    // can recognise rather than a different jolt each time.
    [SerializeField] private float jumpShakeAmount = 2f;
    [SerializeField] private float landShakeAmount = 4f;
    [SerializeField] private float jumpShakeRollAmount = 1f;
    [SerializeField] private float landShakeRollAmount = 2f;
    [SerializeField] private float shakeSpring = 200f;
    [SerializeField] private float shakeDamping = 20f;


    // The rig's root, and near enough the eye point to hang things off. Not exactly
    // it: the rendered camera is a child and may carry an offset of its own, which is
    // why anything that has to be right about where the picture is taken from asks
    // RenderCamera or AimRay instead of this.
    public Transform CameraTransform => cameraPivot;

    // The camera that draws the frame. Anything projecting a world point back onto
    // the screen -- the crosshair -- has to agree with that one, not merely with some
    // camera.
    public Camera RenderCamera => renderCamera != null ? renderCamera : Camera.main;

    // Dead centre of the rendered image, as a world ray. Asked of the camera's own
    // projection rather than built from some transform's forward, so it is the middle
    // of the picture by definition -- under any field of view, aspect, lens shift or
    // Cinemachine arrangement, and whether or not the vcam sits on the pivot.
    //
    // Every transform in the chain is a guess at where the picture is pointing.
    // The camera that draws the picture is not a guess.
    public Ray AimRay
    {
        get
        {
            Camera camera = RenderCamera;

            return camera != null
                ? camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f))
                : new Ray(cameraPivot.position, cameraPivot.forward);
        }
    }

    // Where the player is pointing. Not where the camera is pointing, and not where
    // any particular item is pointing -- this belongs to the character, exists with
    // empty hands, and outlives every item that gets picked up and put down.
    //
    // The one authority. Rounds leave along it, the crosshair is drawn on it, doors
    // and cars are reached through it, autofocus pulls to whatever it lands on. Every
    // one of those used to ask a slightly different question and could therefore
    // disagree -- a reticle in one place and a bullet in another is the class of bug
    // that costs an evening to find, and it is only avoidable by construction.
    //
    // It is the eye ray turned by the free-aim angles, because free aim is a decision
    // the player made about where to point. Bob and sway are deliberately absent:
    // those are a walking animation on the model, not an instruction about where to
    // shoot, and firing down them wanders several degrees at walking pace. Recoil
    // needs no special handling and still climbs, since the fire kick is applied to
    // the camera pivot and so moves the eye ray itself.
    public Ray InteractionRay
    {
        get
        {
            Ray eyeRay = AimRay;

            if (_freeAim == Vector2.zero)
                return eyeRay;

            Camera camera = RenderCamera;
            Transform eye = camera != null ? camera.transform : cameraPivot;

            // Yaw about up, then pitch about right -- the order Unity's Euler uses,
            // so this reproduces the angles HandMotion feeds the item rather than a
            // mirror of them.
            return new Ray(eyeRay.origin,
                Quaternion.AngleAxis(_freeAim.y, eye.up)
                * Quaternion.AngleAxis(_freeAim.x, eye.right)
                * eyeRay.direction);
        }
    }
    public float Pitch { get; private set; }
    public float YawDelta { get; private set; }

    // How far the view actually turned this frame, in degrees -- x yaw, y pitch.
    // Applied rather than requested: against a pitch limit or a car's yaw stop the
    // mouse keeps moving but the view does not, and anything following the view
    // has to stop with it or it will lean into a wall that isn't there. Distinct
    // from YawDelta, which is the body's share of the turn and is zero on a ladder
    // or in a car even while the view is still swinging.
    public Vector2 LookDelta { get; private set; }

    // How far into a lean the view currently is: -1 fully left, 0 upright, 1
    // fully right, already smoothed. The camera and everything parented under it
    // -- the weapon, the aim target -- lean as one rigid piece, so this exists for
    // the one part that can't: the torso, which has to be bent by the Animator or
    // the rig to match. Read it, don't drive it.
    public float PeekAmount => EffectivePeek;

    // The lean actually in force: what the player asked for, less whatever the pitch
    // has taken back. Everything that reads a peek reads this, so nothing can end up
    // leaning by a different amount than everything else.
    private float EffectivePeek => _currentPeek * _peekPitchScale;

    // The same lean in metres rather than as a fraction: how far sideways the view
    // has actually stepped. The body slides by this too, so it is read rather than
    // restated -- a second copy of the distance is a second thing to keep in step,
    // and the two drifting apart is the weapon going one way and the shoulders
    // another.
    public float PeekOffset => EffectivePeek * peekDistance;

    // And the roll, in degrees, signed as the pivot actually applies it. The head
    // bone is turned by this so the silhouette cocks with the view instead of
    // staying square while the camera leans -- which is the whole of what a lean
    // reads as from outside, and the only part of it a shadow can show.
    public float PeekTiltAngle => -EffectivePeek * peekTilt;

    // The roll a full lean is worth, without the lean. Exposed so a pose that wants
    // the same amount of cock for its own reasons -- the head coming over the sights,
    // say -- can take the figure rather than keep a second one that would have to be
    // re-tuned alongside this every time.
    //
    // Signed as a lean to the right, which is the direction the tilt reads in.
    public float PeekTiltMagnitude => -peekTilt;

    // Where the weapon is pointing relative to the view: x is pitch, y is yaw, both
    // in degrees. Read by HandMotion and applied to the item hold -- the offset is
    // decided here because it is subtracted from the view's input, and only the thing
    // receiving that input can withhold it.
    public Vector2 FreeAim => _freeAim;

    // Whether the window is actually in use this frame -- switched on and not shut by
    // aiming. The toggle alone is not the answer: down the sights free aim is off
    // whatever the setting says, and anything tuning itself against the window has to
    // agree with what the window is doing rather than with what was asked for.
    //
    // Read by the hands to decide how much lag and cant to add. With the window open
    // the weapon already swings a long way behind a turn; without it the same turn
    // leaves the weapon dead still, and the sway is the only thing left to say the
    // thing has mass.
    public bool IsFreeAimActive => _isFreeAimActive;

    // The walk cycle's phase, in radians, offset to wherever the footfall sits.
    // Normally the locomotion clip's own, so the view, the hands and the legs are
    // three readings of one number rather than three approximations of each other.
    //
    // Exposed so HandMotion drives the hands off the identical value. Matching
    // frequencies is not enough and never was: two clocks at the same nominal rate
    // still drift, and the drift is exactly the thing that reads as wrong.
    public float BobPhase => _bobPhase;

    // The idle drift's phase, in radians, advancing only while the character is
    // standing still. Exposed for the same reason the bob's is: the hands breathe
    // off this rather than a clock of their own, so the two are one breath rather
    // than two at nearly the same rate drifting through each other.
    public float BreathPhase => _breathTimer;

    private InputAction _lookAction;
    private Vector2 _lookInput;
    private float _currentTilt;
    private float _currentLookTilt;
    private Vector2 _freeAim;
    private float _baseFov;
    private float _climbCameraYaw;
    private bool _wasClimbing;
    private float? _fovOverride;

    private float _breathTimer;
    private Vector3 _currentBreathRotation;
    private float _bobTimer;
    private float _bobPhase;
    private Quaternion _cameraBaseLocalRotation;
    private Vector3 _currentBobRotation;
    private float _shakeOffset;
    private float _shakeVelocity;
    private float _shakeRollOffset;
    private float _shakeRollVelocity;
    private Vector3 _cameraBaseLocalPosition;
    private float _fireKickOffset;
    private float _fireKickVelocity;
    private float _fireKickYawOffset;
    private float _fireKickYawVelocity;
    private float _fireKickSpring = 200f;
    private float _fireKickDamping = 20f;
    private Vector3 _basePivotLocalPosition;
    private Vector3 _headReference;
    private bool _hasHeadReference;
    private bool _warnedAboutHeadAnchor;
    private Vector3 _followedLocalPosition;
    private Vector3 _followVelocity;
    private float _crouchDrop;
    private float _crouchDropVelocity;
    private float _headMountBlend;
    private float _headMountBlendVelocity;
    private float _handsFreeBlend;
    private float _handsFreeBlendVelocity;
    private InputAction _peekAction;
    private InputAction _freeAimToggleAction;
    private bool _isFreeAimActive;
    private float _currentPeek;

    // Starts at 1 so the lean is whole before the first ApplyLook has worked out what
    // the pitch is costing it.
    private float _peekPitchScale = 1f;

    // Lets an equipped item (e.g. Weapon) override FOV while it's active,
    // without PlayerLook needing to know anything about items -- same
    // push-values-in pattern as PlayerAnimator's hand IK targets.
    public void SetFovOverride(float fov) => _fovOverride = fov;
    public void ClearFovOverride() => _fovOverride = null;


    // Weapon-driven recoil kick on the camera itself -- the weapon owns the
    // amount/spring/damping (its recoil "feel") and just pushes them in, the same
    // push-values-in pattern as the FOV override above.
    public void SetFireKickProfile(float spring, float damping)
    {
        _fireKickSpring = spring;
        _fireKickDamping = damping;
    }

    // A shot's punch on the view: an upward pitch kick plus a random left/right yaw
    // kick, both settling back on their own. Velocities are set, not added, so rapid
    // fire cannot stack shots into a runaway kick.
    //
    // This is the whole of a shot's recoil now. It lands on the camera pivot, which
    // both the view and InteractionRay are taken from, so it throws the aim off and
    // has to be brought back -- it is the recoil rather than a picture of one.
    public void AddFireKick(float kickAmount, float horizontalKickAmount)
    {
        // Pitch is the one with a direction of its own: a muzzle climbs, it does not
        // sometimes climb and sometimes dip.
        _fireKickVelocity = -kickAmount;

        // Yaw is signed at random. A weapon wanders off the line it was on rather
        // than off a particular side of it, and a shot that rocked the view the same
        // way every time read as a tic rather than a recoil -- most visible on the
        // first shot of a burst, which always started identically.
        _fireKickYawVelocity = Random.Range(-1f, 1f) * horizontalKickAmount;
    }

    // The head socket in whatever space the pivot's localPosition is written in.
    // Taken from the pivot's actual parent rather than assuming it is this object,
    // so the rig can be nested a level deeper without this quietly reading the
    // wrong space and putting the eyes somewhere off in the world.
    private Vector3 HeadLocalPosition
    {
        get
        {
            Transform pivotSpace = cameraPivot.parent != null ? cameraPivot.parent : transform;
            return pivotSpace.InverseTransformPoint(headAnchor.position);
        }
    }

    // Whether the anchor is a bone of THIS character rather than something left over
    // somewhere else in the scene.
    //
    // The follow survives a wrong anchor because it only ever reads change, and a
    // stationary object contributes none. The mount does not: it puts the view where
    // the anchor is, full stop, so an anchor orphaned by a model swap drops the
    // camera to wherever that object was left -- usually the scene origin, which
    // reads as the view falling to the character's feet the moment its hands are
    // empty, since that is the only time the mount is on.
    //
    // Cheap to check and it fails loudly, which is the whole point: the symptom on
    // its own points at the camera code, and the cause is a reference in the
    // Inspector that nothing else in the game would ever complain about.
    private bool HeadAnchorIsValid
    {
        get
        {
            if (headAnchor == null)
                return false;

            if (headAnchor.IsChildOf(transform))
                return true;

            if (!_warnedAboutHeadAnchor)
            {
                _warnedAboutHeadAnchor = true;
                Debug.LogWarning(
                    $"PlayerLook's Head Anchor ('{headAnchor.name}') is not under {name}, so it is " +
                    "not a bone of this character -- most likely it was orphaned by a model swap. " +
                    "The head mount is off until it is re-parented to the new skeleton's head, " +
                    "because mounting the view on it would put the camera wherever that object " +
                    "has been left.", this);
            }

            return false;
        }
    }

    private void Awake()
    {
        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        _lookAction = playerMap.FindAction("Look");
        _peekAction = playerMap.FindAction("Peek");

        // Not throwIfNotFound: a project without the binding should lose the toggle,
        // not the camera.
        _freeAimToggleAction = playerMap.FindAction("FreeAimToggle");
        if (cameraPivot != null)
        {
            _basePivotLocalPosition = cameraPivot.localPosition;
            _followedLocalPosition = _basePivotLocalPosition;
        }

        if (cinemachineCamera != null)
        {
            _baseFov = cinemachineCamera.Lens.FieldOfView;
            _cameraBaseLocalPosition = cinemachineCamera.transform.localPosition;
            _cameraBaseLocalRotation = cinemachineCamera.transform.localRotation;
        }
    }

    private void OnEnable()
    {
        _lookAction.Enable();
        _peekAction?.Enable();
        _freeAimToggleAction?.Enable();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    // The one job here: catch where the head starts, once.
    //
    // It has to be here rather than in Awake because the Animator poses the skeleton
    // between Update and LateUpdate, so this is the first moment the bone is where
    // the animation actually puts it. Taken in Awake the reference would be the bind
    // pose, and the gap between that and the idle pose would be read as movement the
    // head had already made -- which is exactly what used to drag the view off the
    // position it was placed at, over the first fraction of a second of play.
    private void LateUpdate()
    {
        if (!_hasHeadReference && cameraPivot != null && HeadAnchorIsValid)
        {
            _headReference = HeadLocalPosition;
            _hasHeadReference = true;
        }
    }

    private void OnDisable()
    {
        _lookAction.Disable();
        _peekAction?.Disable();
        _freeAimToggleAction?.Disable();

    }

    private void Update()
    {
        if (_freeAimToggleAction != null && _freeAimToggleAction.WasPressedThisFrame())
            freeAimEnabled = !freeAimEnabled;

        _lookInput = _lookAction.ReadValue<Vector2>();
        ApplyLook();
    }

    // Which of the four is in force, by the same rule HandMotion uses -- aiming wins
    // outright, then crouch, then sprint. Kept identical on purpose: the view and the
    // hands disagreeing about what stance the player is in would be a class of bug
    // with no visible cause.
    //
    // Falls back to the walk figure with no movement component, rather than to zero,
    // so a missing reference leaves the bob working rather than silently absent.
    private float ForStance(StanceValues values)
    {
        if (movement == null)
            return values.walk;

        return movement.IsAiming ? values.aim
            : movement.IsCrouching ? values.crouch
            : movement.IsSprintingStable ? values.sprint
            : values.walk;
    }

    private void ApplyLook()
    {
        bool isClimbing = movement != null && movement.IsClimbingLadder;
        bool isInCar = movement != null && movement.IsInCar;
        bool lockBodyYaw = isClimbing || isInCar;

        if (_wasClimbing && !lockBodyYaw)
        {
            transform.Rotate(Vector3.up * _climbCameraYaw);
            _climbCameraYaw = 0f;
        }
        _wasClimbing = lockBodyYaw;

        float yaw = _lookInput.x * mouseSensitivity;
        float appliedYaw;
        if (lockBodyYaw)
        {
            float yawLimitLeft = isInCar ? carLookYawLimitLeft : climbLookYawLimit;
            float yawLimitRight = isInCar ? carLookYawLimitRight : climbLookYawLimit;

            // The view still turns here, it just turns without the body: the yaw
            // goes into the camera's own offset instead of the capsule. YawDelta
            // stays zero because that is the body's, but the look itself moved.
            float previousClimbCameraYaw = _climbCameraYaw;
            _climbCameraYaw = Mathf.Clamp(_climbCameraYaw + yaw, -yawLimitLeft, yawLimitRight);
            appliedYaw = _climbCameraYaw - previousClimbCameraYaw;
            YawDelta = 0f;
        }
        else
        {
            transform.Rotate(Vector3.up * yaw);
            YawDelta = yaw;
            appliedYaw = yaw;
        }

        // Eased between the two stances on CrouchAmount rather than switched on
        // IsCrouching, so the limit travels with the body it belongs to. Switched, a
        // tighter crouched limit would snap the view down the instant the key went
        // in, while the character was still visibly standing -- the range would
        // arrive before the crouch did.
        float crouchBlend = movement != null ? movement.CrouchAmount : 0f;
        float footPitchUpLimit = Mathf.Lerp(standingPitchUpLimit, crouchPitchUpLimit, crouchBlend);
        float footPitchDownLimit = Mathf.Lerp(standingPitchDownLimit, crouchPitchDownLimit, crouchBlend);

        // Worked out once here and used twice -- for the limits below and for the head
        // mount further down -- because it is one fact about the character and both
        // are answers to it.
        //
        // Eased rather than switched, and this is the half that has to be. Drawing a
        // weapon while craned right back would otherwise clamp the pitch to the
        // tighter limit on the frame the key went in, snapping the view down by the
        // difference. The limit has to close at the speed the arms come up.
        bool handsFree = items != null && !items.AreHandsBusy;
        _handsFreeBlend = Mathf.SmoothDamp(
            _handsFreeBlend, handsFree ? 1f : 0f, ref _handsFreeBlendVelocity, headMountBlendTime);

        footPitchUpLimit = Mathf.Lerp(footPitchUpLimit, noItemPitchUpLimit, _handsFreeBlend);
        footPitchDownLimit = Mathf.Lerp(footPitchDownLimit, noItemPitchDownLimit, _handsFreeBlend);

        float pitchUpLimit = isInCar ? carLookPitchUpLimit : (isClimbing ? climbLookPitchUpLimit : footPitchUpLimit);
        float pitchDownLimit = isInCar ? carLookPitchDownLimit : (isClimbing ? climbLookPitchDownLimit : footPitchDownLimit);

        // The further the camera has turned toward its yaw limit in the car, the less
        // pitch freedom it has left -- mirrors how far you can actually tilt your head
        // up/down once you've already twisted your neck near its rotational limit.
        if (isInCar)
        {
            float yawLimitForRatio = _climbCameraYaw >= 0f ? carLookYawLimitRight : carLookYawLimitLeft;
            float yawRatio = yawLimitForRatio > 0f ? Mathf.Clamp01(Mathf.Abs(_climbCameraYaw) / yawLimitForRatio) : 0f;
            float pitchScale = Mathf.Lerp(1f, carPitchLimitAtMaxYawRatio, yawRatio);
            pitchUpLimit *= pitchScale;
            pitchDownLimit *= pitchScale;
        }
        float previousPitch = Pitch;
        Pitch = Mathf.Clamp(Pitch - _lookInput.y * mouseSensitivity, -pitchUpLimit, pitchDownLimit);

        LookDelta = new Vector2(appliedYaw, Pitch - previousPitch);

        // Measured against whichever limit the view is actually heading for, not a
        // Worked out AFTER the view has turned, and from what it actually turned by.
        //
        // That ordering is the point. Taking the input first and handing back the
        // remainder let the weapon move before the view, but it also meant the mouse
        // did nothing at all until the window filled -- a deadzone, and it read as
        // the camera being slow. Nothing is withheld now: the view turns by the full
        // amount and the weapon swings on top of it, so the two move together and
        // only the weapon has a window.
        //
        // Added rather than subtracted, so the weapon leads the turn: it swings ahead
        // of centre in the direction being turned and settles back to the middle once
        // the mouse stops. The clamp is what stops it running off the screen.
        //
        // Closed down the sights only. That is the one case where it is genuinely
        // wrong -- the whole point of aiming is that the muzzle is on the line, and a
        // window wandering off it defeats the act.
        //
        // Everything else keeps it, including sprinting, the walking carry, and empty
        // hands. This is no longer just where the weapon points: it is where the
        // player is pointing, the direction the crosshair marks and interaction
        // reaches through (see InteractionRay), and that has to exist continuously.
        // Cutting it out during a sprint or a carry would drag the reticle back to
        // centre for reasons that have nothing to do with where the player is
        // looking.
        bool freeAimBlocked = !freeAimEnabled || movement == null || movement.IsAiming;
        _isFreeAimActive = !freeAimBlocked;

        if (freeAimBlocked)
        {
            Vector2 previousFreeAim = _freeAim;
            _freeAim = Vector2.Lerp(_freeAim, Vector2.zero, freeAimCentreSpeed * Time.deltaTime);

            // The view goes to the weapon, not the weapon to the view.
            //
            // Every degree the window gives up is handed straight to the camera, so
            // the barrel's world direction does not move at all while the offset
            // closes -- the crosshair stays on what it was on and the picture swings
            // under it until the two are the same line.
            //
            // Which is the only version of this that respects what the player did.
            // They put the reticle on something; raising the sights is a request to
            // shoot THAT, and swinging the weapon back to the middle of the screen
            // instead answers by taking the aim off it. The offset has to be spent,
            // but which end gives way is a choice, and only one of them keeps the
            // player's decision.
            //
            // Signs fall out for free: the window accumulates LookDelta, so its two
            // axes are already in the same units and the same direction as the pitch
            // and the yaw. What it gives up is exactly what they take on.
            Vector2 released = previousFreeAim - _freeAim;

            Pitch = Mathf.Clamp(Pitch + released.x, -pitchUpLimit, pitchDownLimit);

            if (lockBodyYaw)
            {
                float yawLimitLeft = isInCar ? carLookYawLimitLeft : climbLookYawLimit;
                float yawLimitRight = isInCar ? carLookYawLimitRight : climbLookYawLimit;
                _climbCameraYaw = Mathf.Clamp(_climbCameraYaw + released.y, -yawLimitLeft, yawLimitRight);
            }
            else
            {
                // Reported as body yaw, because it is: the capsule really turned, and
                // PlayerAnimator tracks how far the view leads the body off this. Left
                // out, the model would be turned without the animator being told and
                // the feet would drift by the width of the window.
                transform.Rotate(Vector3.up * released.y);
                YawDelta += released.y;
            }
        }
        else
        {
            // No decay: what the window holds is where the last turn left the weapon,
            // and it stays there until another turn moves it.
            _freeAim.x = Mathf.Clamp(
                _freeAim.x + LookDelta.y * freeAimAmount, -freeAimPitchLimit, freeAimPitchLimit);
            _freeAim.y = Mathf.Clamp(
                _freeAim.y + LookDelta.x * freeAimAmount, -freeAimYawLimit, freeAimYawLimit);
        }

        // Left until here so it reads the pitch the frame actually ends on, including
        // whatever the closing free-aim window just handed it.
        //
        // Measured against whichever limit the view is heading for rather than a
        // fixed angle -- so the lean gives way in proportion to how much of the neck
        // is spent, and a stance with a tighter range spends it sooner. Crouched with
        // a shorter down limit, looking at the floor costs the same fraction of the
        // lean as it does standing, which is the honest reading of it.
        float pitchLimitForRatio = Pitch >= 0f ? pitchDownLimit : pitchUpLimit;
        float pitchRatio = pitchLimitForRatio > 0f
            ? Mathf.Clamp01(Mathf.Abs(Pitch) / pitchLimitForRatio)
            : 0f;

        _peekPitchScale = Mathf.Lerp(1f, peekAtMaxPitchRatio, pitchRatio);

        float targetTilt = movement != null && !movement.IsMovementLocked
            ? -movement.MoveInput.x * tiltAmount
            : 0f;
        _currentTilt = Mathf.Lerp(_currentTilt, targetTilt, tiltSpeed * Time.deltaTime);

        bool isMoving = movement != null && movement.IsGroundedStable && !movement.IsMovementLocked && movement.MoveInput.sqrMagnitude > 0.01f;

        // Purely rotational -- a slow sine drift on pitch/yaw/roll, no position
        // offset, so the camera never feels perfectly locked while standing
        // still. Only while standing still, though -- bob (below) takes over
        // once moving instead of the two stacking.
        if (!isMoving)
            _breathTimer += Time.deltaTime * breathFrequency;

        Vector3 targetBreathRotation = !isMoving
            ? new Vector3(
                Mathf.Sin(_breathTimer * 0.5f) * breathPitchAmount,
                Mathf.Sin(_breathTimer) * breathYawAmount,
                Mathf.Cos(_breathTimer) * breathRollAmount)
            : Vector3.zero;
        _currentBreathRotation = Vector3.Lerp(_currentBreathRotation, targetBreathRotation, breathSmoothing * Time.deltaTime);

        // Also purely rotational -- speeds up and grows with movement state
        // (crouch slower/smaller, sprint faster/bigger). Pitch on the doubled
        // frequency; yaw and roll on the base frequency, 90 degrees apart from
        // each other for a natural circular swing (same pairing as the weapon's
        // own hand bob).
        float bobSpeedRatio = movement != null
            ? Mathf.Min(movement.GaitSpeedRatio, maxBobSpeedRatio)
            : 1f;

        // Only advanced when nothing better is available. With an animator the
        // phase is the clip's, and a clock running alongside it would be a second
        // opinion nobody asked for.
        if (playerAnimator == null && isMoving)
            _bobTimer += Time.deltaTime * bobFallbackFrequency * bobSpeedRatio;

        float rawBobPhase = playerAnimator != null
            ? playerAnimator.LocomotionPhase + bobPhaseOffset * Mathf.Deg2Rad
            : _bobTimer;

        // The cycle is warped rather than advanced evenly, which is what stops the
        // bob reading as a machine. A plain sine spends exactly as long dropping into
        // a footfall as it does rising out of one; a person does not -- weight comes
        // down quickly and is pushed back up slowly, so the two halves of a step take
        // different lengths of time.
        //
        // Adding sin(x) to the phase is that unevenness: it runs the cycle fast
        // through one half and slow through the other while still taking exactly one
        // stride, so nothing drifts. It stays monotonic -- the phase never runs
        // backwards -- for any skew below 1.
        //
        // Done here rather than in HandMotion because this is timing, and timing is
        // shared. The view, the hands and the legs all read this one number, and
        // warping it anywhere downstream would put them back out of step.
        _bobPhase = rawBobPhase + Mathf.Sin(rawBobPhase) * bobCycleSkew;

        // Stance rather than the gait ratio, so a crouch and a sprint are described
        // rather than derived from one another. The ratio above still paces the
        // fallback clock, which is a cadence and not an amount.
        float bobAmount = ForStance(bobIntensity);

        // A rate, not a per-frame amount: LookDelta is degrees this frame, which
        // doubles if the frame does. Dividing it back out is what keeps the same turn
        // canting the view the same way at any frame rate.
        //
        // Negated so the view banks INTO the turn. LookDelta is already the applied
        // yaw rather than the requested one, so this goes still at a yaw limit
        // instead of holding a lean against a wall the player cannot turn past.
        float lookRate = Time.deltaTime > 0f ? LookDelta.x / Time.deltaTime : 0f;

        float targetLookTilt = -Mathf.Clamp(lookRate / lookTiltReferenceRate, -1f, 1f)
            * lookTiltAmount * ForStance(lookTiltIntensity);

        _currentLookTilt = Mathf.Lerp(_currentLookTilt, targetLookTilt, lookTiltSmoothing * Time.deltaTime);

        Vector3 targetBobRotation = isMoving
            ? new Vector3(
                Mathf.Sin(_bobPhase * 2f) * bobPitchAmount * bobAmount,
                Mathf.Sin(_bobPhase) * bobYawAmount * bobAmount,
                Mathf.Cos(_bobPhase) * bobRollAmount * bobAmount)
            : Vector3.zero;
        _currentBobRotation = Vector3.Lerp(_currentBobRotation, targetBobRotation, bobSmoothing * Time.deltaTime);

        // Damped spring kick on jump (up) and landing (down) -- an impulse on
        // velocity snaps it away and settles back like a real spring. Pitch and roll
        // get an offset each but share the spring, so the two settle together and a
        // landing reads as one motion rather than two arriving at their own pace.
        if (movement != null && movement.JumpedThisFrame)
        {
            _shakeVelocity -= jumpShakeAmount;
            _shakeRollVelocity -= jumpShakeRollAmount;
        }

        if (movement != null && movement.LandedThisFrame)
        {
            _shakeVelocity += landShakeAmount;
            _shakeRollVelocity += landShakeRollAmount;
        }

        _shakeVelocity += (-shakeSpring * _shakeOffset - shakeDamping * _shakeVelocity) * Time.deltaTime;
        _shakeOffset += _shakeVelocity * Time.deltaTime;

        _shakeRollVelocity += (-shakeSpring * _shakeRollOffset - shakeDamping * _shakeRollVelocity) * Time.deltaTime;
        _shakeRollOffset += _shakeRollVelocity * Time.deltaTime;

        // Weapon-driven recoil kick -- spring/damping come from whatever item is
        // equipped (pushed via SetFireKickProfile), impulse from AddFireKick per shot.
        _fireKickVelocity += (-_fireKickSpring * _fireKickOffset - _fireKickDamping * _fireKickVelocity) * Time.deltaTime;
        _fireKickOffset += _fireKickVelocity * Time.deltaTime;

        _fireKickYawVelocity += (-_fireKickSpring * _fireKickYawOffset - _fireKickDamping * _fireKickYawVelocity) * Time.deltaTime;
        _fireKickYawOffset += _fireKickYawVelocity * Time.deltaTime;

        if (cameraPivot != null)
        {
            // Critically damped rather than lerped: a spring that never overshoots,
            // so a crouch settles onto its new height instead of dipping past it
            // and coming back. smoothTime is then an honest "how long to catch up"
            // rather than a rate whose meaning changes with the distance.
            // The scene's position plus however far the head has MOVED since play
            // began -- not the head's position with an offset bolted on. The two are
            // the same arithmetic and a completely different result: where the pivot
            // was dragged to is kept exactly, and the skeleton only ever contributes
            // change. Nothing pulls the view off the pose that was authored.
            Vector3 followTarget = _hasHeadReference
                ? _basePivotLocalPosition + (HeadLocalPosition - _headReference)
                : _basePivotLocalPosition;

            _followedLocalPosition = Vector3.SmoothDamp(
                _followedLocalPosition, followTarget, ref _followVelocity, headFollowSmoothTime);

            // Rebuilt from the scene's base every frame rather than nudged from
            // where it was, so nothing can accumulate an offset here over time.
            // The head is the only thing allowed to move the view at all: every
            // other motion it has -- bob, breath, kick -- is rotational and goes
            // in below, where no amount of it can shift the eye point.
            float targetCrouchDrop = movement != null && movement.IsCrouching ? crouchEyeDrop : 0f;
            _crouchDrop = Mathf.SmoothDamp(
                _crouchDrop, targetCrouchDrop, ref _crouchDropVelocity, crouchDropTime);

            // Subtracted after the follow blend rather than folded into its target,
            // so it applies in full even at a headFollowAmount of 0 -- the two are
            // separate systems and turning one off should not take the other with
            // it.
            Vector3 followedPosition = Vector3.Lerp(
                _basePivotLocalPosition, _followedLocalPosition, headFollowAmount)
                - Vector3.up * _crouchDrop;

            // The socket itself, unfiltered and with no authored offset of its own --
            // the view IS the head rather than something trailing it.
            //
            // The crouch drop is left out on purpose. It exists to add a dip the clip
            // does not have; mounted on the bone, the clip's own dip is already the
            // whole of it, and adding more would be describing the same crouch twice.
            // handsFree already answers no when the items reference is unassigned,
            // which is the safe way round: read the other way, a missing reference
            // would mount a weapon straight onto an unfiltered head bone and put the
            // stride back in the barrel -- the exact thing the follow exists to keep
            // out. A ladder and a car need no help either way; neither has hands to
            // ask about.
            bool headMounted = headMountWhenHandsAreFree
                && HeadAnchorIsValid
                && (lockBodyYaw || handsFree);

            _headMountBlend = Mathf.SmoothDamp(
                _headMountBlend, headMounted ? 1f : 0f,
                ref _headMountBlendVelocity, headMountBlendTime);

            Vector3 pivotPosition = _headMountBlend > 0.0001f && HeadAnchorIsValid
                ? Vector3.Lerp(followedPosition, HeadLocalPosition, _headMountBlend)
                : followedPosition;

            // Nowhere to step out to while a ladder or a car has the character: the
            // axis is still being read there and would slide the view off a body
            // that has no way to follow it.
            float targetPeek = _peekAction != null && (movement == null || !movement.IsMovementLocked)
                ? Mathf.Clamp(_peekAction.ReadValue<float>(), -1f, 1f)
                : 0f;
            _currentPeek = Mathf.MoveTowards(_currentPeek, targetPeek, peekSpeed * Time.deltaTime);

            // Sideways in the capsule's own frame, so the slide follows the body
            // rather than the pitch -- stepping out while looking up should still
            // step out sideways, not up and over.
            pivotPosition += Vector3.right * (EffectivePeek * peekDistance);

            cameraPivot.localPosition = pivotPosition;

            // Pre-multiplied, so the roll is about the body's forward rather than
            // the view's. Folded into the Euler's Z instead it would roll about
            // wherever the camera happened to be pointing -- peek while looking at
            // your feet and the view would spin rather than cock to the side.
            //
            // Same sign as the strafe tilt below, so stepping right and leaning
            // right cock the same way instead of cancelling.
            Quaternion peekTiltRotation = Quaternion.AngleAxis(PeekTiltAngle, Vector3.forward);

            // The look, the breath, the bob, and a shot's pitch and yaw kick.
            //
            // The kick belongs here rather than on the rendered camera because this
            // is the transform the aim is taken from: the muzzle climbing and
            // wandering off target IS the recoil, and putting it one level down would
            // make it a picture of a recoil that the shots themselves ignored.
            //
            // The jump and landing shake is not here for the mirror-image reason --
            // it goes on the rendered camera below, because leaving the ground and
            // hitting it again happen to the head and should not move the aim.
            cameraPivot.localRotation = peekTiltRotation * Quaternion.Euler(
                Pitch + _currentBreathRotation.x + _currentBobRotation.x + _fireKickOffset,
                _climbCameraYaw + _currentBreathRotation.y + _currentBobRotation.y + _fireKickYawOffset,
                _currentTilt + _currentBreathRotation.z + _currentBobRotation.z);
        }

        if (cinemachineCamera != null)
        {
            // Everything the weapon should have no part in, on the rendered camera --
            // the one thing under the pivot the items are not parented to. The jump
            // and landing shake, because leaving the ground and hitting it again
            // happen to the head while the hands go on holding what they were
            // holding; and the look tilt, because HandMotion already leans the weapon
            // for the same turn.
            //
            // Rebuilt from the rotation the camera was placed at rather than nudged
            // from where it was left, so a shake that never quite settles can't walk
            // the camera off over a landing or two.
            // Collapsed onto the pivot as the mount takes hold, because the mount puts
            // the PIVOT on the socket and the camera is what has to end up there.
            //
            // This rig's camera sits some way up and forward of the pivot, so without
            // this the view lands that far off the head and the mount looks like it
            // did nothing. Zeroing the offset rather than compensating for it also
            // fixes the second half of the same problem: an eye point held out in
            // front of the pivot swings through an arc every time the view pitches,
            // and a head that orbits twenty centimetres when it nods is not a head.
            cinemachineCamera.transform.localPosition =
                Vector3.Lerp(_cameraBaseLocalPosition, Vector3.zero, _headMountBlend);
            cinemachineCamera.transform.localRotation = _cameraBaseLocalRotation
                * Quaternion.Euler(_shakeOffset, 0f, _shakeRollOffset + _currentLookTilt);
        }

        if (cinemachineCamera != null)
        {
            float fovBoostRatio = 0f;
            if (movement != null)
            {
                if (movement.IsSprintingStable)
                    fovBoostRatio = 1f;
                else if (isInCar)
                    fovBoostRatio = movement.CarSpeedRatio;
            }

            LensSettings lens = cinemachineCamera.Lens;
            float targetFov = _fovOverride ?? (_baseFov + maxFovBoost * fovBoostRatio);
            lens.FieldOfView = Mathf.Lerp(lens.FieldOfView, targetFov, fovSpeed * Time.deltaTime);
            cinemachineCamera.Lens = lens;
        }

        // There is no second field of view here any more. The held item is drawn by
        // this same camera, as an ordinary object, so there is one lens and aiming
        // narrows the weapon along with the world -- which is what a lens does.
    }
}

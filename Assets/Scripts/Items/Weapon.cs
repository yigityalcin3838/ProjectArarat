using System.Collections.Generic;
using Knife.Effects;
using UnityEngine;
using UnityEngine.InputSystem;

// One component for every firearm, with the differences between them declared
// rather than subclassed. A pistol, a rifle and a shotgun disagree about three
// things -- when the trigger counts, how many projectiles leave per pull, and how
// rounds get back in -- and all three are settings.
public class Weapon : Item
{
    // What a trigger pull means, and what has to happen before the next one counts.
    public enum FireMode
    {
        // One shot per press. Holding does nothing.
        SemiAuto,

        // Fires for as long as the trigger is held, paced by the fire clip.
        FullAuto,

        // One press, one round, and rounds go back in one at a time -- each with its
        // own reload animation. A pump gun and a bolt gun are the same weapon as far
        // as this component is concerned: both fire once per pull, both are fed
        // singly, and both throw their case when the action is worked rather than
        // when the shot goes off. What separates them is pelletsPerShot.
        // Written with a dash rather than a slash: Unity reads a slash in an enum
        // label as a submenu separator and would file this as "Bolt Action" nested
        // under a "Shotgun" folder, as though they were two entries.
        [InspectorName("Shotgun - Bolt Action")]
        ShotgunBoltAction,

        // Two shells to fire in turn and then a break-action reload that takes both.
        // Reloading with one still in the chamber is allowed and simply refills.
        DoubleBarrel,
    }

    // itemHold lives on Item and sits under the camera pivot -- that is where this
    // parents while equipped. The holster is the opposite: it stays on the hip
    // bone, because a stowed weapon should ride the body and nobody is looking
    // down a sight through it.
    [SerializeField] private Transform holster;

    [Header("Look")]
    [SerializeField] private InputActionAsset inputActions;
    [SerializeField] private float aimFov = 40f;
    [SerializeField] private PlayerPostProcessEffects postProcessEffects;

    [Header("Fire")]
    [SerializeField] private Animator weaponAnimator;
    [SerializeField] private string fireTrigger = "Fire";
    [SerializeField] private string reloadTrigger = "Reload";
    [SerializeField] private string takeTrigger = "Take";
    [SerializeField] private string holsterTrigger = "Holster";
    [SerializeField] private ParticleGroupEmitter muzzleFlashEmitter;
    [SerializeField] private ParticleGroupEmitter shellEjectEmitter;

    // When the case leaves. On, it goes at the shot, which is what a self-loading
    // action does -- the slide cycles as part of firing and there is nothing else to
    // wait for. Off, only an animation event on a clip can throw it, so a pump or a
    // break action ejects when the shooter works it rather than when the round goes.
    //
    // Left to the fire mode this would be a guess about how a weapon is animated,
    // which is not something the mode knows: a semi-auto animated with an explicit
    // ejection event, or a bolt gun with none, are both perfectly ordinary and the
    // mode would be wrong about each. So it is asked rather than inferred, and with
    // it off and no event on the clip, nothing is ejected at all -- an empty result
    // that is chosen, not a failure.
    [SerializeField] private bool ejectShellOnFire = true;


    [Header("Ammo")]
    // Shells, not projectiles. One trigger pull spends one of these however many
    // things leave the barrel -- a shotgun shell is one round that happens to carry
    // a handful of pellets, and counting the pellets would have a full tube empty
    // itself in a shot.
    // How much the magazine or tube holds, and the only ammunition figure there is:
    // reserve is unlimited, so a reload always has something to put in. What running
    // dry costs is the time to reload, not the ability to.
    [SerializeField] private int magazineCapacity = 12;

    // Everything that differs between a pistol and a shotgun, defaulted so that a
    // weapon set up before any of this existed behaves exactly as it did: one shot
    // per press, one projectile, no spread.
    [SerializeField] private FireMode fireMode = FireMode.SemiAuto;

    // Projectiles per shell. Above 1 they share the shot's spread cone, which is the
    // only thing that stops them landing in the same hole.
    [SerializeField] private int pelletsPerShot = 1;

    // Half-angle of that cone, in degrees. At 0 every pellet goes exactly where the
    // crosshair points, which is right for a rifle and useless for a shotgun.
    [SerializeField] private float spreadAngle = 0f;

    // How hard a hit shoves the part it lands on. Per weapon rather than global,
    // since the whole point is that a slug moves a shoulder and a light round does
    // not -- and per pellet, so a shotgun's push is the sum of what actually
    // connected rather than a flat figure for the trigger pull.
    [SerializeField] private float impactForce = 800f;

    [Header("Hit Detection")]
    [SerializeField] private float maxRange = 100f;

    [Header("Hit Marker")]
    // A box left where the shot landed, alongside the impact effect. The effect is
    // tuned to read as an impact and is gone in a moment; this is for the other
    // question -- did the round go where the crosshair was -- which needs something
    // that stays put and can be walked up to. Debug aid, not a game feature.
    [SerializeField] private bool showHitMarker = true;
    [SerializeField] private Color hitMarkerColor = Color.red;
    [SerializeField] private float hitMarkerSize = 0.1f;

    // Seconds before it disappears. 0 or less leaves it there for good, which is
    // what a grouping test wants.
    [SerializeField] private float hitMarkerDuration = 10f;

    [Header("Movement Effects")]
    [SerializeField] private PlayerMovement movement;

    // Told when this weapon changes to or from its walking carry, which is a moment
    // only the weapon can see: it lands walkPoseDelay after the legs set off and has
    // nothing to do with the step that started them. The jolt's own figures live on
    // HandMotion -- all this supplies is the timing.
    [SerializeField] private HandMotion handMotion;

    [Header("Fire Camera Kick")]
    [SerializeField] private float cameraKickAmount = 1.5f;
    [SerializeField] private float cameraKickHorizontalAmount = 1f;
    [SerializeField] private float cameraKickSpring = 200f;
    [SerializeField] private float cameraKickDamping = 20f;
    [SerializeField] private float cameraRollShakeAmount = 1f;
    [SerializeField] private float cameraRollShakeSpring = 200f;
    [SerializeField] private float cameraRollShakeDamping = 20f;

    [Header("Fire Weapon Roll Shake")]
    // The weapon's own cant on firing, on its own spring. Separate from the camera
    // roll above because the two are no longer the same motion seen twice: the kick
    // lands on the rendered camera alone now, so the weapon riding it is not an
    // option and its recoil has to be stated here or it has none.
    //
    // Which is the better arrangement anyway. A weapon and a head do not recoil
    // alike -- one is braced against a shoulder and the other is watching -- and
    // this is where that difference gets to be said.
    [SerializeField] private float weaponRollShakeAmount = 6f;
    [SerializeField] private float weaponRollShakeSpring = 300f;
    [SerializeField] private float weaponRollShakeDamping = 18f;

    [Header("Weapon Pose")]
    [SerializeField] private float fireHipHoldDuration = 0.2f;
    [SerializeField] private Transform posDeltaPivot;

    [Header("Wall Avoidance")]
    // The far end of the weapon -- the muzzle will do. Everything here is measured
    // against how far in front of the eye this sits, so it does not need to be exact,
    // only to be at the end that hits things first.
    //
    // Empty switches the whole thing off.
    [SerializeField] private Transform muzzlePoint;

    // Where the weapon ends up when there is no room for it, in the same terms as
    // hipPosition and aimPosition -- and, like those, found per weapon rather than
    // shared.
    //
    // Per weapon by necessity, not just by preference. posDeltaPivot's local axes are
    // whatever the model's own build and parenting made them, so the same three
    // numbers mean different things on a pistol and a rifle, and on some of them two
    // axes will both read as roll. There is no set of angles that is right for every
    // weapon, which is why these live on the weapon.
    //
    // Found by rotating the pivot in the scene until the carry looks right and
    // reading the numbers back off it, rather than by reasoning about the axes.
    [SerializeField] private Vector3 wallBlockPosition = new Vector3(0f, -0.05f, -0.25f);
    [SerializeField] private Vector3 wallBlockRotation = new Vector3(-10f, -20f, 12f);

    // How far the weapon has to be intruded on before it is fully in that pose.
    // Below this it is somewhere between the two, in proportion -- which is what
    // makes walking slowly at a wall ease the weapon in rather than snap it.
    [SerializeField] private float wallBlockDepth = 0.35f;

    // Cast as a sphere rather than a line so a doorframe caught at the edge of the
    // barrel still counts. A line finds nothing until the very centre of the muzzle
    // is buried, which is exactly one frame too late.
    [SerializeField] private float wallCheckRadius = 0.08f;

    // Fast in and slower out is deliberate -- arriving at a wall is a collision and
    // should be immediate, leaving one is a decision and can afford to relax. One
    // speed for both has the weapon either lag into the wall or snap out of it.
    [SerializeField] private float wallPullbackInSpeed = 20f;
    [SerializeField] private float wallPullbackOutSpeed = 8f;
    [SerializeField] private Vector3 hipPosition;
    [SerializeField] private Vector3 hipRotation;
    [SerializeField] private Vector3 walkPosition;
    [SerializeField] private Vector3 walkRotation;
    [SerializeField] private float walkTransitionSpeed = 8f;
    [SerializeField] private float walkPoseDelay = 0.15f;
    [SerializeField] private Vector3 runPosition;
    [SerializeField] private Vector3 runRotation;
    [SerializeField] private float runTransitionSpeed = 8f;
    [SerializeField] private Vector3 aimPosition;
    [SerializeField] private Vector3 aimRotation;
    [SerializeField] private float aimTransitionSpeed = 8f;

    [Header("Hand IK")]
    [SerializeField] private PlayerAnimator playerAnimator;
    [SerializeField] private Transform rightGripPoint;
    [SerializeField] private Transform leftGripPoint;
    [SerializeField] private Transform rightElbowHint;
    [SerializeField] private Transform leftElbowHint;
    [SerializeField] private float spineAimWeight = 0.5f;
    [SerializeField] private float chestAimWeight = 0.5f;
    [SerializeField] private float upperChestAimWeight = 0.5f;
    [SerializeField] private float neckAimWeight = 0.5f;

    private Vector3 _originalWorldScale;
    private InputAction _aimAction;
    private InputAction _attackAction;
    private InputAction _reloadAction;
    private int _loadedAmmo;
    private Vector3 _currentAdsPosition;
    private Quaternion _currentAdsRotation;
    private float _fireHipTimer;
    private float _reloadTimer;
    private float _weaponRollShakeOffset;
    private float _weaponRollShakeVelocity;
    private float _drawTimer;
    private bool _wasInWalkPose;
    private bool _isInWalkPose;
    private bool _isFeedingShells;
    private bool _feedInterrupted;
    private float _wallBlockAmount;

    public override bool IsDrawing => _drawTimer > 0f;

    // The feed flag counts as well as the timer: between two shells of a tube reload
    // there is a moment with no clip running, and a swap slipping through it would
    // put the weapon away with the loading half done.
    public override bool IsReloading => _reloadTimer > 0f || _isFeedingShells;

    private void Awake()
    {
        _originalWorldScale = transform.lossyScale;
        SnapTo(holster);

        if (inputActions != null)
        {
            var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
            _aimAction = playerMap.FindAction("Aim", throwIfNotFound: true);
            _attackAction = playerMap.FindAction("Attack", throwIfNotFound: true);
            _reloadAction = playerMap.FindAction("Reload", throwIfNotFound: true);
        }

        _loadedAmmo = magazineCapacity;
    }

    private void OnEnable()
    {
        // A quick re-equip during a still-running holster's IK-release wait
        // must not let that stale coroutine later snap this freshly-drawn
        // weapon back to the holster and clear the hand IK we're about to set.
        if (_holsterIKReleaseCoroutine != null)
        {
            StopCoroutine(_holsterIKReleaseCoroutine);
            _holsterIKReleaseCoroutine = null;

            // That coroutine was the only thing that would have ended the pose
            // hold it started, so cancelling it has to end the hold too.
            playerAnimator?.SetItemPoseHeld(false);
        }

        SnapTo(itemHold);
        SetTriggerIfPresent(takeTrigger);

        // Timed off the clip rather than watched on the animator, because the state
        // isn't reached until the crossfade into it has finished and the draw is
        // already visibly under way before then.
        _drawTimer = GetClipLength(takeTrigger);

        // Pushed here as well as every frame in Update, so the hands have somewhere
        // to be from the moment this is equipped. Update alone is a frame late when
        // the equip happens after it has already run -- which is exactly when it
        // happens on a swap, since the outgoing item releases from a coroutine.
        playerAnimator?.SetRightHandIKTarget(rightGripPoint, rightElbowHint);
        playerAnimator?.SetLeftHandIKTarget(leftGripPoint, leftElbowHint);
        _aimAction?.Enable();
        _attackAction?.Enable();
        _reloadAction?.Enable();

        // Straight into whichever carry the character is already in, because that is
        // a fact about the character and not about this weapon. HandMotion has been
        // counting the walk the whole time, including while another item was held,
        // so a weapon drawn mid-stride arrives in the walking carry rather than
        // climbing to it over a delay the outgoing weapon already served.
        _isInWalkPose = IsCharacterInWalkPose;
        _wasInWalkPose = _isInWalkPose;

        if (posDeltaPivot != null)
        {
            _currentAdsPosition = _isInWalkPose ? walkPosition : hipPosition;
            _currentAdsRotation = Quaternion.Euler(_isInWalkPose ? walkRotation : hipRotation);
            posDeltaPivot.localPosition = _currentAdsPosition;
            posDeltaPivot.localRotation = _currentAdsRotation;
        }
    }

    private void OnDisable()
    {
        _aimAction?.Disable();
        _attackAction?.Disable();
        _reloadAction?.Disable();
        playerLook?.ClearFovOverride();
        postProcessEffects?.SetAiming(false);
        postProcessEffects?.SetReloading(false);

        // The lens has no idea a weapon was put away, so a walk pose left set would
        // outlive the weapon holding it and follow the player around with empty
        // hands.
        postProcessEffects?.SetInWalkPose(false);
        movement?.SetSprintBlocked(false);
        movement?.SetAimSpeedOverride(false);

        // A reload cannot survive the weapon leaving the hand. Both of these count
        // down or clear in Update, which stops running the moment this does, so a
        // weapon put away mid-reload would come back still reporting itself open --
        // and, being unable to finish, stay that way. Ladders and cars stow whatever
        // is held without asking, so this is reachable however well swaps behave.
        _reloadTimer = 0f;
        _isFeedingShells = false;
        _feedInterrupted = false;

        // Cleared immediately (now lerped, so this fades smoothly) rather than
        // delayed to the end of the holster clip like hand IK below -- the aim
        // rig otherwise keeps twisting the spine/chest/neck to track the gun's
        // own barrel direction for the whole clip, and a holster clip swings
        // that barrel down/away, which reads as the torso briefly contorting.
        playerAnimator?.ClearAimRigWeightOverride();

        // Nothing to put away, because nothing was ever taken out. Undo what the
        // spurious enable did and stop -- straight to the hip, no clip, no hold.
        //
        // Clearing the draw timer is the part that matters most. It counts down in
        // Update, which a disabled component does not get, so a timer left running
        // here never reaches zero: IsDrawing stays true for the rest of the session,
        // PlayerItems reads that as a swap permanently in progress, and every slot
        // key is ignored from then on.
        if (IsResetting)
        {
            _drawTimer = 0f;
            SnapTo(holster);
            playerAnimator?.ClearHandIKTargets();
            return;
        }

        SetTriggerIfPresent(holsterTrigger);

        // The character stays in the item pose (Item Layer weight and the
        // IsAiming bool driving that layer's states) for the same clip -- the
        // release below is what ends it, so the two can't drift apart.
        playerAnimator?.SetItemPoseHeld(true);

        // Hand IK keeps tracking the grip points (pistol stays parented under
        // itemHold) for the whole holster clip instead of letting go the
        // instant OnDisable fires -- only once the clip has actually finished
        // does it snap to the real holster socket and let go of IK.
        StartHolsterIKRelease(GetClipLength(holsterTrigger));
    }

    private void Update()
    {
        // Pushed every frame (not just once at equip) so grip point/hint
        // reassignments and the aim weight sliders below stay live-tweakable
        // in the Inspector while playing, instead of only taking effect on
        // the next re-equip.
        playerAnimator?.SetRightHandIKTarget(rightGripPoint, rightElbowHint);
        playerAnimator?.SetLeftHandIKTarget(leftGripPoint, leftElbowHint);
        playerAnimator?.SetAimRigWeightOverride(spineAimWeight, chestAimWeight, upperChestAimWeight, neckAimWeight);

        playerLook?.SetFireKickProfile(cameraKickSpring, cameraKickDamping);
        playerLook?.SetRollShakeProfile(cameraRollShakeSpring, cameraRollShakeDamping);

        if (_drawTimer > 0f)
        {
            _drawTimer -= Time.deltaTime;

            // The view's half at the end of the draw, for the same reason as the
            // reload's: the take clip has been moving the weapon the whole way up
            // and is only now finished with it, so the hands have nothing left to
            // settle from -- but the weapon arriving is a thing worth registering.
            if (_drawTimer <= 0f)
                handMotion?.TriggerCameraShouldering();
        }

        if (_fireHipTimer > 0f)
            _fireHipTimer -= Time.deltaTime;

        bool isFiring = _fireHipTimer > 0f;

        if (_reloadTimer > 0f)
        {
            _reloadTimer -= Time.deltaTime;

            // On the way out, not on the way in: the hand comes back to the weapon
            // once the round is seated, and jolting at the start would land on the
            // one part of a reload where the grip is still where it was.
            //
            // The view's half only. The reload clip is already moving the weapon
            // through this exact moment, and a spring on the hands as well would be
            // two things saying where it is.
            if (_reloadTimer <= 0f)
            {
                CompleteReloadStep();
                handMotion?.TriggerCameraShouldering();
            }
        }

        // Held for full auto, pressed for everything else. That one difference is
        // the whole of what makes a mode automatic -- the rate is already governed
        // by the fire clip below, so holding simply asks again the moment it can be
        // answered.
        bool wantsFire = _attackAction != null && (fireMode == FireMode.FullAuto
            ? _attackAction.IsPressed()
            : _attackAction.WasPerformedThisFrame());

        bool isReloading = _reloadTimer > 0f;

        // The trigger stops a tube being fed, but does not fire. Loading a shotgun a
        // shell at a time is a long commitment and has to be breakable the instant
        // something appears -- what it does not have to be is instant. The round in
        // hand finishes going in, the loop simply stops asking for another, and the
        // next pull is the one that shoots.
        //
        // Firing through the animation instead would be a shot with the weapon
        // visibly open and a hand on the tube.
        if (isReloading && _isFeedingShells && wantsFire)
            _feedInterrupted = true;

        // Reload interrupts an in-progress sprint instead of being blocked by
        // it -- pressing reload while running drops out of the run (below)
        // and reloads anyway.
        //
        // _isFeedingShells is what makes a shotgun keep going: one press starts it
        // and every completed round asks for the next, so the loop lives in the
        // state rather than in a coroutine that would have to be cancelled.
        bool wantsReload = _reloadAction != null && _reloadAction.WasPerformedThisFrame();

        // Asking for a reload clears any earlier interruption, so a tube stopped
        // half-full can be topped up again by pressing again.
        if (wantsReload)
            _feedInterrupted = false;

        if (!isReloading && (wantsReload || _isFeedingShells) && HasRoomToReload)
        {
            BeginReloadStep();
            isReloading = _reloadTimer > 0f;
        }

        // Read here rather than at the top of the frame, because a reload refuses it
        // and the reload's own state is only settled by this point. Holding the
        // button through a reload simply does nothing; letting go and pressing again
        // afterwards is not required, since this is read fresh every frame and picks
        // the aim up the moment the magazine is in.
        bool isAiming = _aimAction != null && _aimAction.IsPressed() && !isReloading;

        postProcessEffects?.SetAiming(isAiming);
        postProcessEffects?.SetReloading(isReloading);

        if (playerLook != null)
        {
            if (isAiming)
                playerLook.SetFovOverride(aimFov);
            else
                playerLook.ClearFovOverride();
        }

        // Can't sprint while aiming down sights, right after firing, or
        // reloading -- IsSprinting is computed live off this blocked flag,
        // so setting it true here immediately drops an in-progress sprint.
        movement?.SetSprintBlocked(isAiming || isFiring || isReloading);
        movement?.SetAimSpeedOverride(isAiming);

        // Fire rate is gated on the weapon animator's own current state, not a
        // manually tracked timer -- a plain clip-length timer ignores the
        // ~0.1s crossfade back to Idle after the clip finishes, so the next
        // shot's SetTrigger could land before the animator had actually
        // reached Idle (still mid-transition, not listening for the trigger
        // yet), letting ammo/effects/hitscan race ahead of what the weapon
        // was visually doing during rapid fire. GetCurrentAnimatorStateInfo
        // still reports "Fire" as current for the whole crossfade back out,
        // so this naturally covers clip length + that transition together.
        bool isFireAnimPlaying = weaponAnimator != null && weaponAnimator.GetCurrentAnimatorStateInfo(0).IsName(fireTrigger);

        // IsDrawing blocks the shot for the length of the take clip. The weapon is
        // still on its way up: a round leaving it there would come out of a barrel
        // pointing at the floor, and the fire clip would cut the draw off partway
        // and leave the weapon wherever it had got to.
        if (wantsFire && !IsDrawing && !isFireAnimPlaying && !isReloading && _loadedAmmo > 0)
        {
            _loadedAmmo--;

            SetTriggerIfPresent(fireTrigger);
            _fireHipTimer = fireHipHoldDuration;

            muzzleFlashEmitter?.Emit(1);

            // Only for the actions that throw the case as they cycle. The rest wait
            // for EmitShell to be called from a clip.
            if (ejectShellOnFire)
                shellEjectEmitter?.Emit(1);

            FireHitscan();

            playerLook?.AddFireKick(cameraKickAmount, cameraKickHorizontalAmount, cameraRollShakeAmount);

            // Set, not added, so holding the trigger can't stack shots into a
            // runaway cant -- the same rule the camera kick follows.
            _weaponRollShakeVelocity = weaponRollShakeAmount;
        }

        // Damped spring pulling the cant back to nothing. An impulse on the velocity
        // snaps it away and lets it settle back, which a lerp toward zero can't do:
        // a lerp only ever approaches, and recoil overshoots.
        _weaponRollShakeVelocity += (-weaponRollShakeSpring * _weaponRollShakeOffset
            - weaponRollShakeDamping * _weaponRollShakeVelocity) * Time.deltaTime;
        _weaponRollShakeOffset += _weaponRollShakeVelocity * Time.deltaTime;

        if (posDeltaPivot != null)
        {
            Vector3 targetPosition;
            Vector3 targetRotationEuler;
            float transitionSpeed;

            bool isMoving = movement != null && movement.MoveInput.sqrMagnitude > 0.01f;
            bool isRunning = movement != null && movement.IsSprintingStable;

            // Entering the walking carry takes movement; staying in it does not.
            //
            // That asymmetry is the whole of it. Walking is what lowers the weapon,
            // but once it is down, standing still is not a reason to bring it back
            // up -- somebody who stops walking does not present their weapon, they
            // just stop walking. Tying the pose to movement frame by frame meant
            // every pause raised it and every step lowered it again, so the muzzle
            // rose and fell over and over on the way down a corridor.
            //
            // What does raise it is something that needs the weapon somewhere else:
            // a shot, a sprint, the sights, a reload. Those clear the pose outright
            // and reset the delay with it, so it has to be walked into again
            // afterwards rather than snapping back the instant they end.
            bool walkPoseBlocked = isAiming || isFiring || isRunning || isReloading;

            if (walkPoseBlocked)
            {
                // Cleared on the character, not just here, so putting this weapon
                // away and drawing another does not hand the replacement a carry
                // this one had just been denied.
                handMotion?.InterruptWalkPose();
                _isInWalkPose = false;
            }
            else
            {
                _isInWalkPose = IsCharacterInWalkPose;
            }

            // Leaving the walking carry only, never settling into it. The two are
            // not the same event despite being the same transition: the weapon
            // eases into the walk pose over walkTransitionSpeed, slowly enough that
            // there is no moment for a jolt to belong to, and comes out of it the
            // instant something takes priority -- a shot, a sprint, the sights going
            // up -- at the far quicker rate those poses use.
            if (_isInWalkPose != _wasInWalkPose)
            {
                _wasInWalkPose = _isInWalkPose;

                if (!_isInWalkPose)
                    handMotion?.TriggerShouldering();
            }

            // Every frame, like aiming and reloading, rather than only on the change.
            // A swap has the outgoing weapon clear this in OnDisable and the incoming
            // one set it on its first update, and pushing only on edges would leave a
            // weapon drawn already in the walking carry never announcing it -- its
            // state never changed, it just started true.
            postProcessEffects?.SetInWalkPose(_isInWalkPose);

            if (isReloading)
            {
                targetPosition = hipPosition;
                targetRotationEuler = hipRotation;
                transitionSpeed = aimTransitionSpeed;
            }
            else if (isAiming)
            {
                targetPosition = aimPosition;
                targetRotationEuler = aimRotation;
                transitionSpeed = aimTransitionSpeed;
            }
            else if (isFiring)
            {
                targetPosition = hipPosition;
                targetRotationEuler = hipRotation;
                transitionSpeed = aimTransitionSpeed;
            }
            else if (isRunning)
            {
                targetPosition = runPosition;
                targetRotationEuler = runRotation;
                transitionSpeed = runTransitionSpeed;
            }
            else if (_isInWalkPose)
            {
                targetPosition = walkPosition;
                targetRotationEuler = walkRotation;
                transitionSpeed = walkTransitionSpeed;
            }
            else
            {
                targetPosition = hipPosition;
                targetRotationEuler = hipRotation;
                transitionSpeed = aimTransitionSpeed;
            }

            _currentAdsPosition = Vector3.Lerp(_currentAdsPosition, targetPosition, transitionSpeed * Time.deltaTime);
            _currentAdsRotation = Quaternion.Slerp(_currentAdsRotation, Quaternion.Euler(targetRotationEuler), transitionSpeed * Time.deltaTime);

            UpdateWallBlock();

            // Blended toward the block pose rather than offset from wherever the
            // weapon happened to be. An offset added to the hip pose and the same
            // offset added to the aim pose land in two different places, and only one
            // of them can be the one that was authored; a blend arrives at the pose
            // itself from either end.
            posDeltaPivot.localPosition =
                Vector3.Lerp(_currentAdsPosition, wallBlockPosition, _wallBlockAmount);

            // Composed on top of the pose rather than lerped into it, so the shake
            // settles on its own spring while the pose goes on easing between hip
            // and aim underneath. Blended into the target instead, the two would be
            // arguing over one value and the recoil would be dragged toward
            // whichever pose was winning.
            // Both cants land on the same axis and about the same point: the weapon's
            // own pivot. The shot's roll is the weapon twisting in the hands; the
            // look tilt is the weapon leaning into a turn. HandMotion works the
            // second one out but deliberately does not apply it -- rolling the hold
            // point would swing the whole weapon around the grip in an arc, where
            // rolling here turns it along its own length, which is what a lean is.
            //
            // The block pose is blended into the base rotation the same way the
            // position is, and the shake and cant compose on top of the result --
            // so a weapon shot while pressed against a wall still recoils, from
            // wherever the wall has put it.
            posDeltaPivot.localRotation =
                Quaternion.Slerp(_currentAdsRotation, Quaternion.Euler(wallBlockRotation), _wallBlockAmount)
                * Quaternion.Euler(0f, 0f,
                    _weaponRollShakeOffset + (handMotion != null ? handMotion.LookTilt : 0f));
        }
    }

    // The character's own pose (Item Layer weight, aim rig) switches
    // instantly on the take/holster command -- only hand IK stays live past
    // that, tracking the grip points for the holster clip's actual duration,
    // so the hands don't let go of the gun before it's visually put away.
    // Stops any still-running release first so a quick re-equip during a
    // holster's tail doesn't have that older coroutine steal IK back off it.
    private Coroutine _holsterIKReleaseCoroutine;

    // That coroutine runs for exactly as long as the holster clip, and clears
    // itself at the end -- so its presence is the answer to "is the gun still
    // being put away".
    public override bool IsStowing => _holsterIKReleaseCoroutine != null;

    private void StartHolsterIKRelease(float duration)
    {
        if (_holsterIKReleaseCoroutine != null)
            StopCoroutine(_holsterIKReleaseCoroutine);

        // A GameObject torn down as part of hierarchy teardown (stopping Play
        // Mode, quitting, a parent getting deactivated) can't host a new
        // coroutine -- and there's nothing left to visually finish for in
        // that case anyway, so just jump straight to the end state instead.
        if (!gameObject.activeInHierarchy)
        {
            SnapTo(holster);
            playerAnimator?.ClearHandIKTargets();
            playerAnimator?.SetItemPoseHeld(false);
            return;
        }

        _holsterIKReleaseCoroutine = StartCoroutine(HolsterIKReleaseRoutine(duration));
    }

    private System.Collections.IEnumerator HolsterIKReleaseRoutine(float duration)
    {
        yield return new WaitForSeconds(duration);

        // Cleared first, before anything is told the hold is over. IsStowing is this
        // reference, and PlayerAnimator asks whether any item is still changing
        // hands when it decides whether to keep the item pose -- so releasing the
        // hold while this still points at a live coroutine has it answer "yes, keep
        // it" and the pose survives a frame longer than the hands and the weapon do.
        //
        // One frame of the character holding a weapon that is already back on the
        // hip, with no hand IK to correct it. Order is the whole of the fix.
        _holsterIKReleaseCoroutine = null;

        SnapTo(holster);
        playerAnimator?.ClearHandIKTargets();
        playerAnimator?.SetItemPoseHeld(false);
    }

    // A tube or a bolt gun takes a round at a time; everything else takes a magazine.
    // The distinction is the whole of what makes those reloads feel like themselves,
    // and it is worth exactly this one property.
    private bool FeedsSingleShells => fireMode == FireMode.ShotgunBoltAction;

    // Pulls the weapon in when there is not enough room in front of the player to
    // hold it out.
    //
    // Measured as the difference between the room the weapon WANTS -- the distance
    // from the eye to its muzzle -- and the room there IS. The wanted length is read
    // from the muzzle every frame rather than stored, so it already accounts for the
    // weapon being further out at the hip than down the sights, and for whatever the
    // pose is doing in between.
    //
    // Cast from the camera rather than from the weapon: the weapon is the thing being
    // moved, so casting from it would move the measurement along with the result and
    // the two would chase each other into an oscillation.
    // Whether the character has been walking long enough to be in the walking carry,
    // regardless of which item is doing the carrying.
    //
    // The delay stays here rather than on HandMotion because it is a property of the
    // weapon -- a pistol and a rifle can reasonably settle at different rates -- while
    // the walk it is measured against belongs to the character.
    //
    // No HandMotion means no walk pose at all, which is deliberate: the alternative
    // is a per-weapon timer that reintroduces exactly the reset-on-swap this exists
    // to remove.
    private bool IsCharacterInWalkPose =>
        handMotion != null && handMotion.WalkTime >= walkPoseDelay;

    private void UpdateWallBlock()
    {
        float target = 0f;

        if (muzzlePoint != null && wallBlockDepth > 0f
            && playerLook != null && playerLook.CameraTransform != null)
        {
            Transform eye = playerLook.CameraTransform;
            float wanted = Vector3.Distance(eye.position, muzzlePoint.position);

            if (Physics.SphereCast(eye.position, wallCheckRadius, eye.forward, out RaycastHit hit,
                    wanted, GameLayers.Queryable, QueryTriggerInteraction.Ignore))
            {
                // How far the wall has come inside the weapon, as a fraction of how
                // far it takes to be fully put away.
                target = Mathf.Clamp01((wanted - hit.distance) / wallBlockDepth);
            }
        }

        // Asymmetric: a wall arrives, it is not approached. Getting out of the way has
        // to keep up with a player walking into it, while coming back out is the
        // weapon being presented again and should take its time.
        float speed = target > _wallBlockAmount ? wallPullbackInSpeed : wallPullbackOutSpeed;

        _wallBlockAmount = Mathf.Lerp(_wallBlockAmount, target, speed * Time.deltaTime);
    }

    // For an animation event, and the only other way a case comes out. Reached
    // through WeaponAnimationEvents rather than directly -- see that class for why
    // the event cannot land here. Safe on any frame of any clip: where the case
    // leaves from and what it looks like are the emitter's business, and this only
    // says when.
    //
    // Not guarded against ejectShellOnFire: a weapon set up with both would throw
    // two, which is visible immediately and easily undone. Refusing quietly would
    // instead look like a broken event.
    public void EmitShell() => shellEjectEmitter?.Emit(1);

    // Somewhere to put it, which with an unlimited reserve is the only question.
    // Reloading a half-empty rifle is a decision the player is allowed to make, so
    // being short of full is enough -- and a full magazine has to refuse, or a
    // shotgun's feed loop would never end.
    private bool HasRoomToReload => _loadedAmmo < magazineCapacity;

    private void BeginReloadStep()
    {
        SetTriggerIfPresent(reloadTrigger);
        _reloadTimer = GetClipLength(reloadTrigger);
        _isFeedingShells = FeedsSingleShells;

        // No clip to wait on -- finish on the spot rather than leaving a shotgun
        // asking for a round it will never be given.
        if (_reloadTimer <= 0f)
            CompleteReloadStep();
    }

    private void CompleteReloadStep()
    {
        // A shotgun takes one round, everything else takes a full magazine. That is
        // the entire difference between the two reloads: one is a gesture repeated
        // until the tube is full, the other is a gesture that fills it.
        _loadedAmmo = FeedsSingleShells
            ? Mathf.Min(_loadedAmmo + 1, magazineCapacity)
            : magazineCapacity;

        // Asks for the next one only while there is still room and nobody has cut in.
        // The interruption is checked here rather than acted on the moment it
        // arrives, which is what lets the round already going in finish going in.
        _isFeedingShells = FeedsSingleShells && HasRoomToReload && !_feedInterrupted;
    }

    // Every trigger this weapon's controller actually declares, cached because
    // Animator.parameters rebuilds its array on each read and these are asked about
    // on the firing path.
    private HashSet<string> _animatorTriggers;

    // Fires a trigger only if the controller has one by that name.
    //
    // The four triggers here are what a fully animated firearm has, not what every
    // firearm has: a weapon partway through being set up, or one that simply has no
    // holster clip, is a legitimate state and not worth an engine error on every
    // single call. Unity's SetTrigger has no opinion about that -- it logs
    // "Parameter 'X' does not exist" and moves on -- so the question is asked here
    // instead, and a missing trigger quietly means no animation.
    private void SetTriggerIfPresent(string triggerName)
    {
        if (weaponAnimator == null || string.IsNullOrEmpty(triggerName))
            return;

        if (_animatorTriggers == null)
        {
            _animatorTriggers = new HashSet<string>();

            foreach (AnimatorControllerParameter parameter in weaponAnimator.parameters)
            {
                if (parameter.type == AnimatorControllerParameterType.Trigger)
                    _animatorTriggers.Add(parameter.name);
            }
        }

        if (_animatorTriggers.Contains(triggerName))
            weaponAnimator.SetTrigger(triggerName);
    }

    // Reads the clip length straight from the Animator Controller already
    // assigned to weaponAnimator, matched by state/clip name -- no separate
    // AnimationClip fields to keep in sync by hand.
    private float GetClipLength(string clipName)
    {
        if (weaponAnimator == null || weaponAnimator.runtimeAnimatorController == null)
            return 0f;

        foreach (AnimationClip clip in weaponAnimator.runtimeAnimatorController.animationClips)
        {
            if (clip.name == clipName)
                return clip.length;
        }

        return 0f;
    }

    private void FireHitscan()
    {
        if (playerLook == null || playerLook.CameraTransform == null)
            return;

        // Straight down the middle of the rendered image -- see PlayerLook.AimRay.
        // Where the crosshair is, is where the round goes, whatever the camera rig
        // is doing to put the crosshair there.
        Ray aimRay = playerLook.AimRay;

        // One trace per projectile, all from the same origin. A shell is one round
        // that carries several, so the ammo has already been spent once by the time
        // this runs and the loop is only about where they land.
        int pellets = Mathf.Max(1, pelletsPerShot);

        for (int i = 0; i < pellets; i++)
        {
            Vector3 direction = ApplySpread(aimRay.direction);

            // Not ~0. Two things have to be skipped, and GameLayers is where the
            // project agrees on them: a character's movement capsule, which wraps the
            // whole body and would otherwise sit in front of every per-bone hitbox
            // and close the gaps between limbs; and debris, which a round should pass
            // straight through. A raycast returns only its nearest hit, so either one
            // wins over the thing actually aimed at.
            //
            // Triggers are skipped as well: Unity's Queries Hit Triggers project
            // setting defaults to on, so without this a bullet stops dead on an
            // invisible door interaction zone or fog volume instead of the wall
            // behind it. Passed per-call rather than switching the project setting,
            // because the interaction raycasts elsewhere DO want to find triggers.
            if (Physics.Raycast(aimRay.origin, direction, out RaycastHit hit, maxRange,
                    GameLayers.Queryable, QueryTriggerInteraction.Ignore))
            {
                SpawnImpactEffect(hit);
                SpawnHitMarker(hit);

                // Per pellet, not per shot. A shotgun that puts twelve pellets into
                // one shoulder should shove it harder than a pistol round does, and
                // the hitbox's own ceiling is what stops that becoming absurd.
                hit.collider.GetComponent<EnemyHitbox>()?.ApplyImpact(direction, impactForce);
            }
        }
    }

    // Deflects a direction by a random amount inside a cone around it. Built off the
    // direction's own frame rather than by adding degrees to world angles, which
    // only approximates a cone and stops approximating it at all when looking
    // straight up or down.
    //
    // Uniform across the disc, so pellets spread evenly rather than crowding the
    // middle -- a shotgun pattern with a dense core is a rifle with extra steps.
    private Vector3 ApplySpread(Vector3 direction)
    {
        if (spreadAngle <= 0f)
            return direction;

        Vector2 offset = Random.insideUnitCircle * spreadAngle;

        return Quaternion.LookRotation(direction)
            * Quaternion.Euler(offset.y, offset.x, 0f)
            * Vector3.forward;
    }

    // Which effect plays, and how it's tinted, isn't the weapon's business --
    // the scene's SurfaceSystem owns that, since it's the thing that knows
    // what was hit. The weapon just reports the hit.
    private void SpawnImpactEffect(RaycastHit hit)
    {
        SurfaceSystem.Instance?.SpawnImpact(hit);
    }

    // Looked up by name and cached, in pipeline order: URP's unlit, then the
    // built-in one, then the last-resort error shader so a marker still shows as
    // magenta rather than silently not existing. A debug aid that can fail to
    // appear is worse than no debug aid, because the absence reads as a miss.
    private static Shader _hitMarkerShader;

    private static Shader HitMarkerShader
    {
        get
        {
            if (_hitMarkerShader != null)
                return _hitMarkerShader;

            _hitMarkerShader = Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Hidden/InternalErrorShader");

            return _hitMarkerShader;
        }
    }

    private void SpawnHitMarker(RaycastHit hit)
    {
        if (!showHitMarker)
            return;

        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = "HitMarker";

        // The collider has to go. CreatePrimitive brings one, and a box collider
        // sitting on the surface would take the next shot aimed at the same spot --
        // every marker would become a wall in front of the one behind it.
        Destroy(marker.GetComponent<Collider>());

        marker.transform.localScale = Vector3.one * hitMarkerSize;

        // Laid flat against the surface and lifted off it by half its own depth, so
        // it sits on the wall rather than half inside it and doesn't z-fight.
        marker.transform.SetPositionAndRotation(
            hit.point + hit.normal * (hitMarkerSize * 0.5f),
            Quaternion.LookRotation(hit.normal));

        Renderer renderer = marker.GetComponent<Renderer>();

        // Built outright rather than tinting whatever CreatePrimitive handed over.
        // That default comes from the render pipeline and is lit, so a marker in
        // shadow or facing away from the sun is a dark grey box on a dark grey wall
        // -- present, and impossible to see. Unlit, it is the colour it was asked
        // for from any angle in any light, which is the entire point of it.
        Material material = new Material(HitMarkerShader);
        material.color = hitMarkerColor;

        // URP's own colour property. Material.color only reaches it when the shader
        // tags one as its main colour, and a fallback shader might not.
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", hitMarkerColor);

        renderer.material = material;

        // Nothing about a debug marker should reach the lighting: a shadow cast by
        // one is a shadow the shot didn't make.
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        if (hitMarkerDuration > 0f)
            Destroy(marker, hitMarkerDuration);
    }

    public int LoadedAmmo => _loadedAmmo;
    public int MagazineCapacity => magazineCapacity;

    private void OnGUI()
    {
        const float width = 240f;
        const float height = 24f;
        const float margin = 20f;

        GUI.Label(
            new Rect(Screen.width - width - margin, Screen.height - height - margin, width, height),
            $"Ammo: {_loadedAmmo} / {magazineCapacity}");
    }

    private void SnapTo(Transform anchor)
    {
        // Holster/itemHold sit under the camera/spine, so this fires as a side
        // effect while Unity tears down that hierarchy on stopping Play Mode or
        // quitting -- reparenting mid-teardown is invalid and throws.
        if (anchor == null || IsApplicationQuitting)
            return;

        transform.SetParent(anchor, worldPositionStays: false);
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;

        // Compensate for whatever scale the new parent's own ancestor chain
        // carries, so the pistol keeps the same visual (world) size in the
        // holster and in the hand even if those two anchors sit under
        // differently-scaled bones.
        Vector3 parentScale = anchor.lossyScale;
        transform.localScale = new Vector3(
            parentScale.x != 0f ? _originalWorldScale.x / parentScale.x : _originalWorldScale.x,
            parentScale.y != 0f ? _originalWorldScale.y / parentScale.y : _originalWorldScale.y,
            parentScale.z != 0f ? _originalWorldScale.z / parentScale.z : _originalWorldScale.z);
    }
}

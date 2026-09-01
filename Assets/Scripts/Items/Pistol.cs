using Knife.Effects;
using UnityEngine;
using UnityEngine.InputSystem;

public class Pistol : Item
{
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

    [Header("Ammo")]
    [SerializeField] private int magazineCapacity = 12;

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

    [Header("Fire Camera Kick")]
    [SerializeField] private float cameraKickAmount = 1.5f;
    [SerializeField] private float cameraKickHorizontalAmount = 1f;
    [SerializeField] private float cameraKickSpring = 200f;
    [SerializeField] private float cameraKickDamping = 20f;
    [SerializeField] private float cameraRollShakeAmount = 1f;
    [SerializeField] private float cameraRollShakeSpring = 200f;
    [SerializeField] private float cameraRollShakeDamping = 20f;

    [Header("Weapon Pose")]
    [SerializeField] private float fireHipHoldDuration = 0.2f;
    [SerializeField] private Transform posDeltaPivot;
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
    private int[] _magazineAmmo = new int[2];
    private int _activeMagazineIndex;
    private Vector3 _currentAdsPosition;
    private Quaternion _currentAdsRotation;
    private float _fireHipTimer;
    private float _reloadTimer;
    private float _moveTimer;

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

        // Two magazines, swapped (not refilled) on reload -- picking up ammo/mags
        // from the ground later is what actually replenishes them.
        _magazineAmmo[0] = magazineCapacity;
        _magazineAmmo[1] = magazineCapacity;
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
        weaponAnimator?.SetTrigger(takeTrigger);
        _aimAction?.Enable();
        _attackAction?.Enable();
        _reloadAction?.Enable();

        if (posDeltaPivot != null)
        {
            _currentAdsPosition = hipPosition;
            _currentAdsRotation = Quaternion.Euler(hipRotation);
            posDeltaPivot.localPosition = hipPosition;
            posDeltaPivot.localRotation = _currentAdsRotation;
        }
    }

    private void OnDisable()
    {
        weaponAnimator?.SetTrigger(holsterTrigger);
        _aimAction?.Disable();
        _attackAction?.Disable();
        _reloadAction?.Disable();
        playerLook?.ClearFovOverride();
        postProcessEffects?.SetAiming(false);
        movement?.SetSprintBlocked(false);
        movement?.SetAimSpeedOverride(false);

        // Cleared immediately (now lerped, so this fades smoothly) rather than
        // delayed to the end of the holster clip like hand IK below -- the aim
        // rig otherwise keeps twisting the spine/chest/neck to track the gun's
        // own barrel direction for the whole clip, and a holster clip swings
        // that barrel down/away, which reads as the torso briefly contorting.
        playerAnimator?.ClearAimRigWeightOverride();

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
        bool isAiming = _aimAction != null && _aimAction.IsPressed();
        postProcessEffects?.SetAiming(isAiming);

        // Pushed every frame (not just once at equip) so grip point/hint
        // reassignments and the aim weight sliders below stay live-tweakable
        // in the Inspector while playing, instead of only taking effect on
        // the next re-equip.
        playerAnimator?.SetRightHandIKTarget(rightGripPoint, rightElbowHint);
        playerAnimator?.SetLeftHandIKTarget(leftGripPoint, leftElbowHint);
        playerAnimator?.SetAimRigWeightOverride(spineAimWeight, chestAimWeight, upperChestAimWeight, neckAimWeight);

        playerLook?.SetFireKickProfile(cameraKickSpring, cameraKickDamping);
        playerLook?.SetRollShakeProfile(cameraRollShakeSpring, cameraRollShakeDamping);

        if (_aimAction != null && playerLook != null)
        {
            if (isAiming)
                playerLook.SetFovOverride(aimFov);
            else
                playerLook.ClearFovOverride();
        }

        if (_fireHipTimer > 0f)
            _fireHipTimer -= Time.deltaTime;

        bool isFiring = _fireHipTimer > 0f;

        if (_reloadTimer > 0f)
            _reloadTimer -= Time.deltaTime;

        bool isReloading = _reloadTimer > 0f;

        // Reload interrupts an in-progress sprint instead of being blocked by
        // it -- pressing reload while running drops out of the run (below)
        // and reloads anyway.
        if (_reloadAction != null && _reloadAction.WasPerformedThisFrame() && !isReloading)
        {
            _activeMagazineIndex = 1 - _activeMagazineIndex;
            weaponAnimator?.SetTrigger(reloadTrigger);
            _reloadTimer = GetClipLength(reloadTrigger);
            isReloading = true;
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

        if (_attackAction != null && _attackAction.WasPerformedThisFrame() && !isFireAnimPlaying && !isReloading && _magazineAmmo[_activeMagazineIndex] > 0)
        {
            _magazineAmmo[_activeMagazineIndex]--;

            weaponAnimator?.SetTrigger(fireTrigger);
            _fireHipTimer = fireHipHoldDuration;

            muzzleFlashEmitter?.Emit(1);
            shellEjectEmitter?.Emit(1);

            FireHitscan();

            playerLook?.AddFireKick(cameraKickAmount, cameraKickHorizontalAmount, cameraRollShakeAmount);
        }

        if (posDeltaPivot != null)
        {
            Vector3 targetPosition;
            Vector3 targetRotationEuler;
            float transitionSpeed;

            bool isMoving = movement != null && movement.MoveInput.sqrMagnitude > 0.01f;
            bool isRunning = movement != null && movement.IsSprintingStable;

            // Only counts up while walk is actually the pose that would apply --
            // aiming, firing, reloading or breaking into a run all take priority
            // over walk, so any of those resets the timer too, not just stopping.
            // That way walk pose always has to wait out the delay again after
            // being interrupted by anything, not just resume instantly once clear.
            bool wantsWalkPose = isMoving && !isAiming && !isFiring && !isRunning && !isReloading;
            if (wantsWalkPose)
                _moveTimer += Time.deltaTime;
            else
                _moveTimer = 0f;

            bool isMovingPastDelay = wantsWalkPose && _moveTimer >= walkPoseDelay;

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
            else if (isMovingPastDelay)
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

            posDeltaPivot.localPosition = _currentAdsPosition;
            posDeltaPivot.localRotation = _currentAdsRotation;
        }
    }

    // The character's own pose (PistolAim layer weight, aim rig) switches
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
        SnapTo(holster);
        playerAnimator?.ClearHandIKTargets();
        playerAnimator?.SetItemPoseHeld(false);
        _holsterIKReleaseCoroutine = null;
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

        // No layer mask -- hits anything solid. Triggers are skipped
        // explicitly: Unity's Queries Hit Triggers project setting defaults to
        // on, so without this a bullet stops dead on an invisible door
        // interaction zone or fog volume instead of the wall behind it.
        // Passed per-call rather than switching the project setting, because
        // the interaction raycasts below DO want to find triggers.
        if (Physics.Raycast(aimRay, out RaycastHit hit, maxRange, ~0, QueryTriggerInteraction.Ignore))
        {
            SpawnImpactEffect(hit);
            SpawnHitMarker(hit);
        }
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

    private void OnGUI()
    {
        const float width = 240f;
        const float height = 24f;
        const float margin = 20f;
        float x = Screen.width - width - margin;

        GUI.Label(new Rect(x, Screen.height - height * 2f - margin, width, height), $"Magazine 1{(_activeMagazineIndex == 0 ? " (active)" : "")}: {_magazineAmmo[0]} / {magazineCapacity}");
        GUI.Label(new Rect(x, Screen.height - height - margin, width, height), $"Magazine 2{(_activeMagazineIndex == 1 ? " (active)" : "")}: {_magazineAmmo[1]} / {magazineCapacity}");
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

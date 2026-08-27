using Knife.Effects;
using UnityEngine;
using UnityEngine.InputSystem;

public class Pistol : Item
{
    [SerializeField] private Transform holster;
    [SerializeField] private Transform handGrip;

    [Header("Look")]
    [SerializeField] private InputActionAsset inputActions;
    [SerializeField] private PlayerLook playerLook;
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

    [Header("Bloom")]
    [SerializeField] private float maxBloomAngle = 5f;
    [SerializeField] private float bloomPerShot = 1f;
    [SerializeField] private float bloomRecoverySpeed = 5f;

    [Header("Impact Effect")]
    [SerializeField] private GameObject impactEffectPrefab;

    [Header("Movement Effects")]
    [SerializeField] private PlayerMovement movement;

    [Header("Strafe Tilt")]
    [SerializeField] private Transform tiltTarget;
    [SerializeField] private float tiltAmount = 10f;
    [SerializeField] private float tiltSpeed = 8f;

    [Header("Hand Bob")]
    [SerializeField] private Transform bobTarget;
    [SerializeField] private float bobFrequency = 6f;
    [SerializeField] private float bobHorizontalAmount = 0.01f;
    [SerializeField] private float bobVerticalAmount = 0.02f;
    [SerializeField] private float bobPitchAmount = 2f;
    [SerializeField] private float bobYawAmount = 2f;
    [SerializeField] private float bobRollAmount = 2f;
    [SerializeField] private float bobSprintSpeedMultiplier = 1.5f;
    [SerializeField] private float bobSprintIntensityMultiplier = 1.5f;
    [SerializeField] private float aimBobMultiplier = 0.3f;
    [SerializeField] private float bobSmoothing = 8f;

    [Header("Rotational Sway")]
    [SerializeField] private Transform swayTarget;
    [SerializeField] private float swayAmount = 4f;
    [SerializeField] private float swaySmoothing = 8f;

    [Header("Positional Sway")]
    [SerializeField] private Transform positionSwayTarget;
    [SerializeField] private float positionSwayHorizontalAmount = 0.01f;
    [SerializeField] private float positionSwayVerticalAmount = 0.01f;
    [SerializeField] private float positionSwayForwardAmount = 0.01f;
    [SerializeField] private float positionSwaySmoothing = 8f;

    [Header("Breathing")]
    [SerializeField] private Transform breathTarget;
    [SerializeField] private float breathFrequency = 1f;
    [SerializeField] private float breathHorizontalAmount = 0.005f;
    [SerializeField] private float breathVerticalAmount = 0.01f;
    [SerializeField] private float breathPitchAmount = 1f;
    [SerializeField] private float breathRollAmount = 1f;
    [SerializeField] private float aimBreathMultiplier = 0.3f;
    [SerializeField] private float breathSmoothing = 4f;

    [Header("Jump / Land Kick")]
    [SerializeField] private Transform kickTarget;
    [SerializeField] private float jumpKickAmount = 0.6f;
    [SerializeField] private float landKickAmount = 1.2f;
    [SerializeField] private float jumpKickRotationAmount = 30f;
    [SerializeField] private float landKickRotationAmount = 60f;
    [SerializeField] private float kickSpring = 200f;
    [SerializeField] private float kickDamping = 20f;

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
    private static bool _isQuitting;
    private InputAction _aimAction;
    private InputAction _attackAction;
    private InputAction _lookAction;
    private InputAction _reloadAction;
    private int[] _magazineAmmo = new int[2];
    private int _activeMagazineIndex;
    private float _currentBloom;
    private float _currentTilt;
    private Vector3 _bobBaseLocalPosition;
    private Quaternion _bobBaseLocalRotation;
    private Vector3 _currentBobOffset;
    private Vector3 _currentBobRotation;
    private float _bobTimer;
    private Quaternion _baseSwayLocalRotation;
    private Vector2 _currentSway;
    private Vector3 _basePositionSwayLocalPosition;
    private Vector3 _currentPositionSway;
    private Vector3 _currentAdsPosition;
    private Quaternion _currentAdsRotation;
    private Vector3 _breathBaseLocalPosition;
    private Quaternion _breathBaseLocalRotation;
    private Vector3 _currentBreathOffset;
    private Vector2 _currentBreathRotation;
    private float _breathTimer;
    private Vector3 _kickBaseLocalPosition;
    private Quaternion _kickBaseLocalRotation;
    private float _kickOffset;
    private float _kickVelocity;
    private float _kickRotationOffset;
    private float _kickRotationVelocity;
    private float _fireHipTimer;
    private float _reloadTimer;
    private float _moveTimer;

    private void Awake()
    {
        _originalWorldScale = transform.lossyScale;
        SnapTo(holster);

        if (bobTarget != null)
        {
            _bobBaseLocalPosition = bobTarget.localPosition;
            _bobBaseLocalRotation = bobTarget.localRotation;
        }

        if (swayTarget != null)
            _baseSwayLocalRotation = swayTarget.localRotation;

        if (positionSwayTarget != null)
            _basePositionSwayLocalPosition = positionSwayTarget.localPosition;

        if (breathTarget != null)
        {
            _breathBaseLocalPosition = breathTarget.localPosition;
            _breathBaseLocalRotation = breathTarget.localRotation;
        }

        if (kickTarget != null)
        {
            _kickBaseLocalPosition = kickTarget.localPosition;
            _kickBaseLocalRotation = kickTarget.localRotation;
        }

        if (inputActions != null)
        {
            var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
            _aimAction = playerMap.FindAction("Aim", throwIfNotFound: true);
            _attackAction = playerMap.FindAction("Attack", throwIfNotFound: true);
            _lookAction = playerMap.FindAction("Look", throwIfNotFound: true);
            _reloadAction = playerMap.FindAction("Reload", throwIfNotFound: true);
        }

        // Two magazines, swapped (not refilled) on reload -- picking up ammo/mags
        // from the ground later is what actually replenishes them.
        _magazineAmmo[0] = magazineCapacity;
        _magazineAmmo[1] = magazineCapacity;
    }

    private void OnApplicationQuit() => _isQuitting = true;

    private void OnEnable()
    {
        // A quick re-equip during a still-running holster's IK-release wait
        // must not let that stale coroutine later snap this freshly-drawn
        // weapon back to the holster and clear the hand IK we're about to set.
        if (_holsterIKReleaseCoroutine != null)
        {
            StopCoroutine(_holsterIKReleaseCoroutine);
            _holsterIKReleaseCoroutine = null;
        }

        SnapTo(handGrip);
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

        // Hand IK keeps tracking the grip points (pistol stays parented under
        // handGrip) for the whole holster clip instead of letting go the
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

        _currentBloom = Mathf.Max(0f, _currentBloom - bloomRecoverySpeed * Time.deltaTime);

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

            _currentBloom = Mathf.Min(maxBloomAngle, _currentBloom + bloomPerShot);

            playerLook?.AddFireKick(cameraKickAmount, cameraKickHorizontalAmount, cameraRollShakeAmount);
        }

        // If tiltTarget sits under something with its own MatchCameraRotation
        // (e.g. HandGrip), that component's Match Roll needs to be OFF so it
        // doesn't overwrite the Z value set here every LateUpdate.
        if (movement != null && tiltTarget != null)
        {
            float targetTilt = -movement.MoveInput.x * tiltAmount;
            _currentTilt = Mathf.Lerp(_currentTilt, targetTilt, tiltSpeed * Time.deltaTime);

            Vector3 euler = tiltTarget.localEulerAngles;
            euler.z = _currentTilt;
            tiltTarget.localEulerAngles = euler;
        }

        if (movement != null && bobTarget != null)
        {
            bool isMoving = movement.IsGrounded && !movement.IsMovementLocked && movement.MoveInput.sqrMagnitude > 0.01f;
            bool isSprintingBob = movement.IsSprintingStable;
            float speedRatio = isSprintingBob ? bobSprintSpeedMultiplier : 1f;
            float intensityRatio = (isSprintingBob ? bobSprintIntensityMultiplier : 1f) * (isAiming ? aimBobMultiplier : 1f);

            if (isMoving)
                _bobTimer += Time.deltaTime * bobFrequency * speedRatio;

            Vector3 targetBobOffset = isMoving
                ? new Vector3(Mathf.Cos(_bobTimer) * bobHorizontalAmount * intensityRatio, Mathf.Sin(_bobTimer * 2f) * bobVerticalAmount * intensityRatio, 0f)
                : Vector3.zero;
            // Pitch (tip up/down) follows the vertical bob's phase; yaw and roll
            // (side-to-side swing and tilt) follow the horizontal bob's phase,
            // 90 degrees apart from each other for a natural circular swing.
            Vector3 targetBobRotation = isMoving
                ? new Vector3(
                    Mathf.Sin(_bobTimer * 2f) * bobPitchAmount * intensityRatio,
                    Mathf.Sin(_bobTimer) * bobYawAmount * intensityRatio,
                    Mathf.Cos(_bobTimer) * bobRollAmount * intensityRatio)
                : Vector3.zero;

            _currentBobOffset = Vector3.Lerp(_currentBobOffset, targetBobOffset, bobSmoothing * Time.deltaTime);
            _currentBobRotation = Vector3.Lerp(_currentBobRotation, targetBobRotation, bobSmoothing * Time.deltaTime);

            bobTarget.localPosition = _bobBaseLocalPosition + _currentBobOffset;
            bobTarget.localRotation = _bobBaseLocalRotation * Quaternion.Euler(_currentBobRotation.x, _currentBobRotation.y, _currentBobRotation.z);
        }

        // Reads Look directly rather than through PlayerLook -- PlayerLook already
        // owns Enable/Disable for this action, so this never touches its enabled
        // state (holstering the pistol must not disable camera look).
        if (swayTarget != null && _lookAction != null)
        {
            Vector2 lookInput = _lookAction.ReadValue<Vector2>();
            Vector2 targetSway = new Vector2(-lookInput.y, lookInput.x) * swayAmount;
            _currentSway = Vector2.Lerp(_currentSway, targetSway, swaySmoothing * Time.deltaTime);

            swayTarget.localRotation = _baseSwayLocalRotation * Quaternion.Euler(_currentSway.x, _currentSway.y, 0f);
        }

        if (positionSwayTarget != null && movement != null)
        {
            Vector3 targetPositionSway = new Vector3(
                -movement.MoveInput.x * positionSwayHorizontalAmount,
                -movement.Velocity.y * positionSwayVerticalAmount,
                -movement.MoveInput.y * positionSwayForwardAmount);

            _currentPositionSway = Vector3.Lerp(_currentPositionSway, targetPositionSway, positionSwaySmoothing * Time.deltaTime);
            positionSwayTarget.localPosition = _basePositionSwayLocalPosition + _currentPositionSway;
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

        if (breathTarget != null)
        {
            float breathIntensity = isAiming ? aimBreathMultiplier : 1f;

            _breathTimer += Time.deltaTime * breathFrequency;
            Vector3 targetBreathOffset = new Vector3(
                Mathf.Sin(_breathTimer) * breathHorizontalAmount * breathIntensity,
                Mathf.Sin(_breathTimer * 0.5f) * breathVerticalAmount * breathIntensity,
                0f);
            Vector2 targetBreathRotation = new Vector2(
                Mathf.Sin(_breathTimer * 0.5f) * breathPitchAmount * breathIntensity,
                Mathf.Sin(_breathTimer) * breathRollAmount * breathIntensity);

            _currentBreathOffset = Vector3.Lerp(_currentBreathOffset, targetBreathOffset, breathSmoothing * Time.deltaTime);
            _currentBreathRotation = Vector2.Lerp(_currentBreathRotation, targetBreathRotation, breathSmoothing * Time.deltaTime);

            breathTarget.localPosition = _breathBaseLocalPosition + _currentBreathOffset;
            breathTarget.localRotation = _breathBaseLocalRotation * Quaternion.Euler(_currentBreathRotation.x, 0f, _currentBreathRotation.y);
        }

        if (kickTarget != null && movement != null)
        {
            if (movement.JumpedThisFrame)
            {
                _kickVelocity += jumpKickAmount;
                _kickRotationVelocity -= jumpKickRotationAmount;
            }
            if (movement.LandedThisFrame)
            {
                _kickVelocity -= landKickAmount;
                _kickRotationVelocity += landKickRotationAmount;
            }

            // Simple damped spring pulling the offsets back to 0 -- an impulse on
            // velocity makes them snap away and settle back, instead of a plain
            // lerp which can't overshoot/oscillate.
            _kickVelocity += (-kickSpring * _kickOffset - kickDamping * _kickVelocity) * Time.deltaTime;
            _kickOffset += _kickVelocity * Time.deltaTime;

            _kickRotationVelocity += (-kickSpring * _kickRotationOffset - kickDamping * _kickRotationVelocity) * Time.deltaTime;
            _kickRotationOffset += _kickRotationVelocity * Time.deltaTime;

            Vector3 pos = _kickBaseLocalPosition;
            pos.y += _kickOffset;
            kickTarget.localPosition = pos;
            kickTarget.localRotation = _kickBaseLocalRotation * Quaternion.Euler(_kickRotationOffset, 0f, 0f);
        }
    }

    // The character's own pose (PistolAim layer weight, aim rig) switches
    // instantly on the take/holster command -- only hand IK stays live past
    // that, tracking the grip points for the holster clip's actual duration,
    // so the hands don't let go of the gun before it's visually put away.
    // Stops any still-running release first so a quick re-equip during a
    // holster's tail doesn't have that older coroutine steal IK back off it.
    private Coroutine _holsterIKReleaseCoroutine;

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
            return;
        }

        _holsterIKReleaseCoroutine = StartCoroutine(HolsterIKReleaseRoutine(duration));
    }

    private System.Collections.IEnumerator HolsterIKReleaseRoutine(float duration)
    {
        yield return new WaitForSeconds(duration);
        SnapTo(holster);
        playerAnimator?.ClearHandIKTargets();
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

        Transform cam = playerLook.CameraTransform;

        // Spread within a cone around the camera's forward direction, widening
        // with bloom -- this is what the shot actually travels along, not just a
        // visual effect, so bloom genuinely affects where hits land.
        Vector3 spreadDirection = Quaternion.Euler(Random.Range(-_currentBloom, _currentBloom), Random.Range(-_currentBloom, _currentBloom), 0f) * cam.forward;

        // No layer mask -- hits anything with a collider.
        if (Physics.Raycast(cam.position, spreadDirection, out RaycastHit hit, maxRange))
            SpawnImpactEffect(hit.point, hit.normal);
    }

    // One prefab for every surface for now. Later this should pick a
    // different prefab per surface material instead of always the same one.
    // The prefab's own effect (e.g. a ParticleSystem with Stop Action set to
    // Destroy) is responsible for cleaning itself up -- no manual timer here.
    private void SpawnImpactEffect(Vector3 point, Vector3 normal)
    {
        if (impactEffectPrefab == null)
            return;

        Instantiate(impactEffectPrefab, point, Quaternion.LookRotation(normal));
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
        // Holster/handGrip sit under the camera/spine, so this fires as a side
        // effect while Unity tears down that hierarchy on stopping Play Mode or
        // quitting -- reparenting mid-teardown is invalid and throws.
        if (anchor == null || _isQuitting)
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

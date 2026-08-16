using FMOD.Studio;
using FMODUnity;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class Car : MonoBehaviour
{
    [Header("Anchor Points")]
    [SerializeField] private Transform doorLeft;
    [SerializeField] private Transform frontLeft;
    [SerializeField] private Transform leftHandGrip;
    [SerializeField] private Transform rightHandGrip;
    [SerializeField] private Transform handBrakeGrip;
    [SerializeField] private Transform hornGrip;
    [SerializeField] private Transform gearGrip;

    [Header("Body")]
    [SerializeField] private Collider[] bodyColliders;

    [Header("Wheel Colliders")]
    [SerializeField] private WheelCollider frontLeftWheelCollider;
    [SerializeField] private WheelCollider frontRightWheelCollider;
    [SerializeField] private WheelCollider rearLeftWheelCollider;
    [SerializeField] private WheelCollider rearRightWheelCollider;

    [Header("Wheel Meshes")]
    [SerializeField] private Transform frontLeftWheelMesh;
    [SerializeField] private Transform frontRightWheelMesh;
    [SerializeField] private Transform rearLeftWheelMesh;
    [SerializeField] private Transform rearRightWheelMesh;

    [Header("Input")]
    [SerializeField] private InputActionAsset inputActions;

    [Header("Driving")]
    [SerializeField] private float motorTorque = 1500f;
    [SerializeField] private float reverseTorqueMultiplier = 0.5f;
    [SerializeField] private float maxSpeedKmh = 120f;

    [Header("Braking")]
    [SerializeField] private float brakeTorque = 3000f;
    [SerializeField] private float parkBrakeTorque = 3000f;

    [Header("Steering")]
    [SerializeField] private float maxSteerAngle = 30f;
    [SerializeField] private float steerSpeed = 120f;
    [SerializeField] private Transform steeringWheel;
    [SerializeField] private float steeringWheelRotationMultiplier = 3f;
    [SerializeField] private Vector3 steeringWheelRotationAxis = Vector3.forward;

    [Header("Handbrake")]
    [SerializeField] private float handbrakeTorque = 8000f;

    [Header("Animation")]
    [SerializeField] private Animator carAnimator;

    [Header("Vehicle State")]
    [SerializeField] private float handArrivalDelay = 0.15f;

    [Header("Anti-Roll")]
    [SerializeField] private float antiRollStiffness = 5000f;

    [Header("Drift")]
    [SerializeField] private float driftSidewaysStiffness = 0.5f;

    [Header("UI")]
    [SerializeField] private TMP_Text speedText;

    [Header("Audio")]
    [SerializeField] private StudioEventEmitter engineStartEmitter;
    [SerializeField] private StudioEventEmitter engineIdleEmitter;
    [SerializeField] private StudioEventEmitter engineStopEmitter;
    [SerializeField] private StudioEventEmitter handbrakeOnEmitter;
    [SerializeField] private StudioEventEmitter handbrakeOffEmitter;
    [SerializeField] private StudioEventEmitter hornEmitter;

    private static readonly int HandbrakePullHash = Animator.StringToHash("HandbrakePull");
    private static readonly int HandbrakeReleaseHash = Animator.StringToHash("HandbrakeRelease");
    private static readonly int GearShiftFrontHash = Animator.StringToHash("GearShiftFront");
    private static readonly int GearShiftBackHash = Animator.StringToHash("GearShiftBack");
    private static readonly int VehicleResetHash = Animator.StringToHash("VehicleReset");
    private static readonly int GearReturnToIdleHash = Animator.StringToHash("GearReturnToIdle");
    private const float GearInputThreshold = 0.1f;
    private const float GearShiftStopThreshold = 0.5f;

    private enum GearPosition { Idle, Front, Back }
    private enum HandbrakePosition { Idle, Up, Down }
    private enum ShutdownPhase { None, Handbrake, Gear, Engine, Complete }

    public Vector3 DoorLeft => doorLeft.position;
    public Vector3 FrontLeft => frontLeft.position;
    public Vector3 Forward => doorLeft.forward;
    public Vector3 Up => doorLeft.up;
    public Transform LeftHandGrip => leftHandGrip;
    public Transform RightHandGrip => rightHandGrip;
    public Transform HandBrakeGrip => handBrakeGrip;
    public Transform HornGrip => hornGrip;
    public Transform GearGrip => gearGrip;

    public bool IsHandbrakeHeld { get; private set; }
    public bool IsHornPressed { get; private set; }
    public float SpeedRatio => Mathf.Clamp01(_rb.linearVelocity.magnitude / (maxSpeedKmh / 3.6f));

    public bool IsHandbrakeAnimating
    {
        get
        {
            if (_handbrakeAnimatedState != _handbrakeTarget && _handbrakeTarget != HandbrakePosition.Idle)
                return true;

            if (carAnimator == null)
                return false;

            if (carAnimator.IsInTransition(0))
            {
                AnimatorStateInfo nextInfo = carAnimator.GetNextAnimatorStateInfo(0);
                if (nextInfo.IsName("HandbrakeUp") || nextInfo.IsName("HandbrakeDown"))
                    return true;
            }

            AnimatorStateInfo currentInfo = carAnimator.GetCurrentAnimatorStateInfo(0);
            bool isHandbrakeState = currentInfo.IsName("HandbrakeUp") || currentInfo.IsName("HandbrakeDown");
            return isHandbrakeState && currentInfo.normalizedTime < 1f;
        }
    }

    public bool IsGearAnimating
    {
        get
        {
            if (_shutdownPhase == ShutdownPhase.Gear)
                return true;

            if (_gearAnimatedState != _gearTarget && _gearTarget != GearPosition.Idle)
                return true;

            if (carAnimator == null)
                return false;

            const int gearLayer = 1;

            if (carAnimator.IsInTransition(gearLayer))
            {
                AnimatorStateInfo nextInfo = carAnimator.GetNextAnimatorStateInfo(gearLayer);
                if (nextInfo.IsName("GearFront") || nextInfo.IsName("GearBack"))
                    return true;
            }

            AnimatorStateInfo currentInfo = carAnimator.GetCurrentAnimatorStateInfo(gearLayer);
            bool isGearState = currentInfo.IsName("GearFront") || currentInfo.IsName("GearBack");
            return isGearState && currentInfo.normalizedTime < 1f;
        }
    }

    private bool IsGearReturnAnimating
    {
        get
        {
            if (carAnimator == null)
                return false;

            const int gearLayer = 1;

            if (carAnimator.IsInTransition(gearLayer))
            {
                AnimatorStateInfo nextInfo = carAnimator.GetNextAnimatorStateInfo(gearLayer);
                if (nextInfo.IsName("GearFrontReverse") || nextInfo.IsName("GearBackReverse"))
                    return true;
            }

            AnimatorStateInfo currentInfo = carAnimator.GetCurrentAnimatorStateInfo(gearLayer);
            return currentInfo.IsName("GearFrontReverse") || currentInfo.IsName("GearBackReverse");
        }
    }

    public bool IsReadyToExit => _shutdownPhase == ShutdownPhase.Complete;

    public void RequestShutdown()
    {
        _shutdownRequested = true;
    }

    public bool IsBeingDriven
    {
        get => _isBeingDriven;
        set
        {
            if (_isBeingDriven == value)
                return;

            _isBeingDriven = value;

            if (_isBeingDriven)
            {
                StartEngineAudio();
                ResetVehicleState();
            }
            else
            {
                SettleToIdleDisplay();
            }
        }
    }

    private Rigidbody _rb;
    private InputAction _moveAction;
    private InputAction _handbrakeAction;
    private InputAction _hornAction;
    private float _currentSteerAngle;
    private float _rearLeftNormalSidewaysStiffness;
    private float _rearRightNormalSidewaysStiffness;
    private Quaternion _steeringWheelBaseRotation;
    private bool _isBeingDriven;
    private bool _waitingForEngineStart;
    private bool _waitingForEngineStop;
    private bool _pendingAutoRelease;

    private GearPosition _gearTarget;
    private GearPosition _gearAnimatedState;
    private float _gearAnimationDelayTimer;

    private HandbrakePosition _handbrakeTarget;
    private bool _lastHandbrakeHeld;
    private HandbrakePosition _handbrakeAnimatedState;
    private float _handbrakeAnimationDelayTimer;

    private bool _shutdownRequested;
    private ShutdownPhase _shutdownPhase;
    private bool _gearReturnTriggered;
    private float _gearReturnDelayTimer;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        _rearLeftNormalSidewaysStiffness = rearLeftWheelCollider.sidewaysFriction.stiffness;
        _rearRightNormalSidewaysStiffness = rearRightWheelCollider.sidewaysFriction.stiffness;

        if (steeringWheel != null)
            _steeringWheelBaseRotation = steeringWheel.localRotation;

        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        _moveAction = playerMap.FindAction("Move");
        _handbrakeAction = playerMap.FindAction("Jump");
        _hornAction = playerMap.FindAction("Attack");

        foreach (Collider bodyCollider in bodyColliders)
        {
            if (bodyCollider == null)
                continue;

            Physics.IgnoreCollision(bodyCollider, frontLeftWheelCollider);
            Physics.IgnoreCollision(bodyCollider, frontRightWheelCollider);
            Physics.IgnoreCollision(bodyCollider, rearLeftWheelCollider);
            Physics.IgnoreCollision(bodyCollider, rearRightWheelCollider);
        }
    }

    private void OnEnable()
    {
        _moveAction.Enable();
        _handbrakeAction.Enable();
        _hornAction.Enable();
    }

    private void OnDisable()
    {
        _moveAction.Disable();
        _handbrakeAction.Disable();
        _hornAction.Disable();
    }

    private void Update()
    {
        UpdateSpeedText();
        UpdateEngineAudio();
        UpdateHorn();
    }

    private void UpdateHorn()
    {
        if (!IsBeingDriven)
        {
            IsHornPressed = false;
            return;
        }

        if (_hornAction.WasPressedThisFrame())
        {
            IsHornPressed = true;
            if (hornEmitter != null)
                hornEmitter.Play();
        }
        else if (_hornAction.WasReleasedThisFrame())
        {
            IsHornPressed = false;
            if (hornEmitter != null)
                hornEmitter.Stop();
        }
    }

    private void StartEngineAudio()
    {
        if (engineStartEmitter == null)
        {
            PlayEngineIdle();
            return;
        }

        engineStartEmitter.Play();
        _waitingForEngineStart = true;
    }

    private void UpdateEngineAudio()
    {
        if (_waitingForEngineStart)
        {
            engineStartEmitter.EventInstance.getPlaybackState(out PLAYBACK_STATE startState);
            if (startState == PLAYBACK_STATE.STOPPED)
            {
                _waitingForEngineStart = false;
                PlayEngineIdle();
            }
        }

        if (_waitingForEngineStop)
        {
            engineStopEmitter.EventInstance.getPlaybackState(out PLAYBACK_STATE stopState);
            if (stopState == PLAYBACK_STATE.STOPPED)
                _waitingForEngineStop = false;
        }
    }

    private void PlayEngineIdle()
    {
        if (engineIdleEmitter != null)
            engineIdleEmitter.Play();
    }

    private void StopEngineAudio()
    {
        _waitingForEngineStart = false;

        if (engineStartEmitter != null)
            engineStartEmitter.Stop();

        if (engineIdleEmitter != null)
            engineIdleEmitter.Stop();

        if (engineStopEmitter != null)
        {
            engineStopEmitter.Play();
            _waitingForEngineStop = true;
        }

        if (hornEmitter != null)
            hornEmitter.Stop();

        IsHornPressed = false;
    }

    private void PlayHandbrakeFeedback(bool engaged)
    {
        StudioEventEmitter emitter = engaged ? handbrakeOnEmitter : handbrakeOffEmitter;
        if (emitter != null)
            emitter.Play();

        if (carAnimator != null)
            carAnimator.SetTrigger(engaged ? HandbrakePullHash : HandbrakeReleaseHash);
    }

    private void SettleToIdleDisplay()
    {
        _handbrakeTarget = HandbrakePosition.Idle;
        _handbrakeAnimatedState = HandbrakePosition.Idle;
        _gearTarget = GearPosition.Idle;
        _gearAnimatedState = GearPosition.Idle;

        if (carAnimator != null)
            carAnimator.SetTrigger(VehicleResetHash);
    }

    private void ResetVehicleState()
    {
        _gearTarget = GearPosition.Idle;
        _gearAnimatedState = GearPosition.Idle;
        _gearAnimationDelayTimer = 0f;

        _handbrakeTarget = HandbrakePosition.Idle;
        _lastHandbrakeHeld = _handbrakeAction != null && _handbrakeAction.IsPressed();
        _handbrakeAnimatedState = HandbrakePosition.Idle;
        _handbrakeAnimationDelayTimer = 0f;

        _shutdownRequested = false;
        _shutdownPhase = ShutdownPhase.None;
        _gearReturnTriggered = false;
        _gearReturnDelayTimer = 0f;
        _pendingAutoRelease = true;

        if (carAnimator != null)
            carAnimator.SetTrigger(VehicleResetHash);
    }

    private void FixedUpdate()
    {
        Vector2 input = IsBeingDriven ? _moveAction.ReadValue<Vector2>() : Vector2.zero;
        bool handbrake = IsBeingDriven && (_handbrakeAction.IsPressed() || _shutdownPhase != ShutdownPhase.None);
        IsHandbrakeHeld = handbrake;

        if (IsBeingDriven)
        {
            if (_pendingAutoRelease && !_waitingForEngineStart)
            {
                _pendingAutoRelease = false;
                _handbrakeTarget = HandbrakePosition.Down;
            }

            if (_shutdownRequested && _shutdownPhase == ShutdownPhase.None && !IsGearAnimating && !IsHandbrakeAnimating)
            {
                _shutdownRequested = false;
                _shutdownPhase = ShutdownPhase.Handbrake;
                _handbrakeTarget = HandbrakePosition.Up;
            }

            UpdateHandbrakeState(handbrake);

            if (_shutdownPhase == ShutdownPhase.None)
                UpdateGearState(input.y);
            else
                UpdateShutdown();
        }

        ApplySteering(input.x);
        ApplyDrive(input.y, handbrake);
        ApplyDriftGrip(handbrake);
        ClampSpeed();
        ApplyAntiRoll(frontLeftWheelCollider, frontRightWheelCollider);
        ApplyAntiRoll(rearLeftWheelCollider, rearRightWheelCollider);
        SyncWheelMeshes();
    }

    private void UpdateShutdown()
    {
        switch (_shutdownPhase)
        {
            case ShutdownPhase.Handbrake:
                if (!IsHandbrakeAnimating)
                {
                    _shutdownPhase = ShutdownPhase.Gear;
                    _gearReturnTriggered = false;
                    _gearReturnDelayTimer = 0f;
                }
                break;

            case ShutdownPhase.Gear:
                UpdateGearReturnToIdle();
                break;

            case ShutdownPhase.Engine:
                if (!_waitingForEngineStop)
                    _shutdownPhase = ShutdownPhase.Complete;
                break;
        }
    }

    private void UpdateGearReturnToIdle()
    {
        if (_gearTarget == GearPosition.Idle)
        {
            _shutdownPhase = ShutdownPhase.Engine;
            StopEngineAudio();
            return;
        }

        if (!_gearReturnTriggered)
        {
            _gearReturnDelayTimer += Time.fixedDeltaTime;
            if (_gearReturnDelayTimer < handArrivalDelay)
                return;

            _gearReturnTriggered = true;
            if (carAnimator != null)
                carAnimator.SetTrigger(GearReturnToIdleHash);
            return;
        }

        if (IsGearReturnAnimating)
            return;

        _gearTarget = GearPosition.Idle;
        _gearAnimatedState = GearPosition.Idle;
        _shutdownPhase = ShutdownPhase.Engine;
        StopEngineAudio();
    }

    private void UpdateHandbrakeState(bool held)
    {
        if (held != _lastHandbrakeHeld && !IsGearAnimating)
        {
            _lastHandbrakeHeld = held;
            HandbrakePosition target = held ? HandbrakePosition.Up : HandbrakePosition.Down;

            if (target != _handbrakeTarget)
            {
                _handbrakeTarget = target;
                _handbrakeAnimationDelayTimer = 0f;
            }
        }

        if (_handbrakeAnimatedState != _handbrakeTarget && _handbrakeTarget != HandbrakePosition.Idle)
        {
            _handbrakeAnimationDelayTimer += Time.fixedDeltaTime;
            if (_handbrakeAnimationDelayTimer >= handArrivalDelay)
            {
                _handbrakeAnimatedState = _handbrakeTarget;
                PlayHandbrakeFeedback(_handbrakeTarget == HandbrakePosition.Up);
            }
        }
    }

    private void UpdateGearState(float throttleInput)
    {
        GearPosition desired = _gearTarget;
        if (throttleInput > GearInputThreshold)
            desired = GearPosition.Front;
        else if (throttleInput < -GearInputThreshold)
            desired = GearPosition.Back;

        bool canShift = _gearTarget == GearPosition.Idle ||
            Mathf.Abs(Vector3.Dot(_rb.linearVelocity, transform.forward)) <= GearShiftStopThreshold;

        if (desired != _gearTarget && canShift && !IsHandbrakeAnimating)
        {
            _gearTarget = desired;
            _gearAnimationDelayTimer = 0f;
        }

        if (_gearAnimatedState != _gearTarget && _gearTarget != GearPosition.Idle)
        {
            _gearAnimationDelayTimer += Time.fixedDeltaTime;
            if (_gearAnimationDelayTimer >= handArrivalDelay)
            {
                _gearAnimatedState = _gearTarget;
                if (carAnimator != null)
                    carAnimator.SetTrigger(_gearTarget == GearPosition.Front ? GearShiftFrontHash : GearShiftBackHash);
            }
        }
    }

    private void ApplySteering(float steerInput)
    {
        float targetSteerAngle = steerInput * maxSteerAngle;
        _currentSteerAngle = Mathf.MoveTowards(_currentSteerAngle, targetSteerAngle, steerSpeed * Time.fixedDeltaTime);
        frontLeftWheelCollider.steerAngle = _currentSteerAngle;
        frontRightWheelCollider.steerAngle = _currentSteerAngle;

        if (steeringWheel != null)
            steeringWheel.localRotation = _steeringWheelBaseRotation * Quaternion.AngleAxis(-_currentSteerAngle * steeringWheelRotationMultiplier, steeringWheelRotationAxis);
    }

    private void ApplyDrive(float throttleInput, bool handbrake)
    {
        float forwardSpeed = Vector3.Dot(_rb.linearVelocity, transform.forward);
        bool isBraking = IsBeingDriven && ((throttleInput > 0.01f && forwardSpeed < -0.5f) || (throttleInput < -0.01f && forwardSpeed > 0.5f));

        bool canDrive = IsBeingDriven && _handbrakeTarget == HandbrakePosition.Down && !IsHandbrakeAnimating &&
            ((throttleInput > 0f && _gearTarget == GearPosition.Front && !IsGearAnimating) ||
             (throttleInput < 0f && _gearTarget == GearPosition.Back && !IsGearAnimating));

        float torque = 0f;
        if (!isBraking && canDrive)
        {
            torque = throttleInput * motorTorque;
            if (throttleInput < 0f)
                torque *= reverseTorqueMultiplier;
        }

        rearLeftWheelCollider.motorTorque = torque;
        rearRightWheelCollider.motorTorque = torque;

        float baseBrake = !IsBeingDriven ? parkBrakeTorque : (isBraking ? brakeTorque : 0f);
        frontLeftWheelCollider.brakeTorque = baseBrake;
        frontRightWheelCollider.brakeTorque = baseBrake;
        rearLeftWheelCollider.brakeTorque = handbrake ? handbrakeTorque : baseBrake;
        rearRightWheelCollider.brakeTorque = handbrake ? handbrakeTorque : baseBrake;
    }

    private void ApplyDriftGrip(bool handbrake)
    {
        SetSidewaysStiffness(rearLeftWheelCollider, handbrake ? driftSidewaysStiffness : _rearLeftNormalSidewaysStiffness);
        SetSidewaysStiffness(rearRightWheelCollider, handbrake ? driftSidewaysStiffness : _rearRightNormalSidewaysStiffness);
    }

    private static void SetSidewaysStiffness(WheelCollider wheelCollider, float stiffness)
    {
        WheelFrictionCurve friction = wheelCollider.sidewaysFriction;
        friction.stiffness = stiffness;
        wheelCollider.sidewaysFriction = friction;
    }

    private void ClampSpeed()
    {
        float maxSpeedMs = maxSpeedKmh / 3.6f;
        Vector3 velocity = _rb.linearVelocity;

        if (velocity.magnitude > maxSpeedMs)
            _rb.linearVelocity = velocity.normalized * maxSpeedMs;
    }

    private void ApplyAntiRoll(WheelCollider wheelL, WheelCollider wheelR)
    {
        float travelL = 1f;
        float travelR = 1f;

        bool groundedL = wheelL.GetGroundHit(out WheelHit hitL);
        if (groundedL)
            travelL = (-wheelL.transform.InverseTransformPoint(hitL.point).y - wheelL.radius) / wheelL.suspensionDistance;

        bool groundedR = wheelR.GetGroundHit(out WheelHit hitR);
        if (groundedR)
            travelR = (-wheelR.transform.InverseTransformPoint(hitR.point).y - wheelR.radius) / wheelR.suspensionDistance;

        float antiRollForce = (travelL - travelR) * antiRollStiffness;

        if (groundedL)
            _rb.AddForceAtPosition(wheelL.transform.up * -antiRollForce, wheelL.transform.position);
        if (groundedR)
            _rb.AddForceAtPosition(wheelR.transform.up * antiRollForce, wheelR.transform.position);
    }

    private void SyncWheelMeshes()
    {
        SyncWheelMesh(frontLeftWheelCollider, frontLeftWheelMesh);
        SyncWheelMesh(frontRightWheelCollider, frontRightWheelMesh);
        SyncWheelMesh(rearLeftWheelCollider, rearLeftWheelMesh);
        SyncWheelMesh(rearRightWheelCollider, rearRightWheelMesh);
    }

    private static void SyncWheelMesh(WheelCollider wheelCollider, Transform wheelMesh)
    {
        if (wheelMesh == null)
            return;

        wheelCollider.GetWorldPose(out Vector3 position, out Quaternion rotation);
        wheelMesh.SetPositionAndRotation(position, rotation);
    }

    private void UpdateSpeedText()
    {
        if (speedText == null)
            return;

        if (!IsBeingDriven)
        {
            speedText.text = string.Empty;
            return;
        }

        Vector3 horizontalVelocity = _rb.linearVelocity;
        horizontalVelocity.y = 0f;
        int speedKmh = Mathf.RoundToInt(horizontalVelocity.magnitude * 3.6f);
        speedText.text = $"{speedKmh} km/h";
    }
}

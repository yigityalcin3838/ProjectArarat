using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class Car : MonoBehaviour
{
    [Header("Anchor Points")]
    [SerializeField] private Transform doorLeft;
    [SerializeField] private Transform frontLeft;

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
    [SerializeField] private float throttleSmoothSpeed = 2f;
    [SerializeField] private float parkBrakeTorque = 3000f;
    [SerializeField] private float maxSpeedKmh = 120f;

    [Header("Steering")]
    [SerializeField] private float maxSteerAngle = 30f;
    [SerializeField] private float steerSpeed = 120f;

    [Header("Handbrake")]
    [SerializeField] private float handbrakeTorque = 8000f;

    [Header("Anti-Roll")]
    [SerializeField] private float antiRollStiffness = 5000f;

    [Header("UI")]
    [SerializeField] private TMP_Text speedText;

    public Vector3 DoorLeft => doorLeft.position;
    public Vector3 FrontLeft => frontLeft.position;
    public Vector3 Forward => doorLeft.forward;
    public Vector3 Up => doorLeft.up;

    public bool IsBeingDriven { get; set; }

    private Rigidbody _rb;
    private InputAction _moveAction;
    private InputAction _handbrakeAction;
    private float _currentThrottle;
    private float _currentSteerAngle;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();

        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        _moveAction = playerMap.FindAction("Move");
        _handbrakeAction = playerMap.FindAction("Jump");

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
    }

    private void OnDisable()
    {
        _moveAction.Disable();
        _handbrakeAction.Disable();
    }

    private void Update()
    {
        UpdateSpeedText();
    }

    private void FixedUpdate()
    {
        Vector2 input = IsBeingDriven ? _moveAction.ReadValue<Vector2>() : Vector2.zero;
        bool handbrake = IsBeingDriven && _handbrakeAction.IsPressed();

        ApplySteering(input.x);
        ApplyDrive(input.y, handbrake);
        ClampSpeed();
        ApplyAntiRoll(frontLeftWheelCollider, frontRightWheelCollider);
        ApplyAntiRoll(rearLeftWheelCollider, rearRightWheelCollider);
        SyncWheelMeshes();
    }

    private void ApplySteering(float steerInput)
    {
        float targetSteerAngle = steerInput * maxSteerAngle;
        _currentSteerAngle = Mathf.MoveTowards(_currentSteerAngle, targetSteerAngle, steerSpeed * Time.fixedDeltaTime);
        frontLeftWheelCollider.steerAngle = _currentSteerAngle;
        frontRightWheelCollider.steerAngle = _currentSteerAngle;
    }

    private void ApplyDrive(float throttleInput, bool handbrake)
    {
        _currentThrottle = Mathf.MoveTowards(_currentThrottle, throttleInput, throttleSmoothSpeed * Time.fixedDeltaTime);

        float torque = _currentThrottle * motorTorque;
        if (_currentThrottle < 0f)
            torque *= reverseTorqueMultiplier;

        rearLeftWheelCollider.motorTorque = torque;
        rearRightWheelCollider.motorTorque = torque;

        float brake = IsBeingDriven ? 0f : parkBrakeTorque;
        frontLeftWheelCollider.brakeTorque = brake;
        frontRightWheelCollider.brakeTorque = brake;
        rearLeftWheelCollider.brakeTorque = handbrake ? handbrakeTorque : brake;
        rearRightWheelCollider.brakeTorque = handbrake ? handbrakeTorque : brake;
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

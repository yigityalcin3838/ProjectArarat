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

    [Header("Driving")]
    [SerializeField] private InputActionAsset inputActions;
    [SerializeField] private float motorTorque = 1500f;
    [SerializeField] private float brakeTorque = 3000f;
    [SerializeField] private float handbrakeTorque = 8000f;
    [SerializeField] private float coastBrakeTorque = 800f;
    [SerializeField] private float throttleSmoothSpeed = 2f;
    [SerializeField] private float maxSteerAngle = 30f;
    [SerializeField] private float steerSpeed = 120f;

    [Header("Stability")]
    [SerializeField] private Vector3 centerOfMassOffset = new Vector3(0f, -0.5f, 0f);
    [SerializeField] private float angularDamping = 3f;

    [Header("Low Speed Stability")]
    [SerializeField] private float lowSpeedThreshold = 5f;
    [SerializeField] private int substepsBelowThreshold = 12;
    [SerializeField] private int substepsAboveThreshold = 1;

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
        _rb.centerOfMass += centerOfMassOffset;
        _rb.angularDamping = angularDamping;

        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        _moveAction = playerMap.FindAction("Move");
        _handbrakeAction = playerMap.FindAction("Jump");

        frontLeftWheelCollider.ConfigureVehicleSubsteps(lowSpeedThreshold, substepsBelowThreshold, substepsAboveThreshold);
        frontRightWheelCollider.ConfigureVehicleSubsteps(lowSpeedThreshold, substepsBelowThreshold, substepsAboveThreshold);
        rearLeftWheelCollider.ConfigureVehicleSubsteps(lowSpeedThreshold, substepsBelowThreshold, substepsAboveThreshold);
        rearRightWheelCollider.ConfigureVehicleSubsteps(lowSpeedThreshold, substepsBelowThreshold, substepsAboveThreshold);

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

    private void FixedUpdate()
    {
        Vector2 input = IsBeingDriven ? _moveAction.ReadValue<Vector2>() : Vector2.zero;
        bool handbrake = IsBeingDriven && _handbrakeAction.IsPressed();
        bool isCoasting = IsBeingDriven && !handbrake && Mathf.Abs(input.y) < 0.01f;

        _currentThrottle = Mathf.MoveTowards(_currentThrottle, input.y, throttleSmoothSpeed * Time.fixedDeltaTime);

        float targetSteerAngle = input.x * maxSteerAngle;
        _currentSteerAngle = Mathf.MoveTowards(_currentSteerAngle, targetSteerAngle, steerSpeed * Time.fixedDeltaTime);
        frontLeftWheelCollider.steerAngle = _currentSteerAngle;
        frontRightWheelCollider.steerAngle = _currentSteerAngle;

        float torque = _currentThrottle * motorTorque;
        rearLeftWheelCollider.motorTorque = torque;
        rearRightWheelCollider.motorTorque = torque;

        float brake = !IsBeingDriven ? brakeTorque : (isCoasting ? coastBrakeTorque : 0f);
        frontLeftWheelCollider.brakeTorque = brake;
        frontRightWheelCollider.brakeTorque = brake;
        rearLeftWheelCollider.brakeTorque = handbrake ? handbrakeTorque : brake;
        rearRightWheelCollider.brakeTorque = handbrake ? handbrakeTorque : brake;

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
}

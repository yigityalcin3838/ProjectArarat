using UnityEngine;

[DefaultExecutionOrder(100)]
public class PlayerCameraFollow : MonoBehaviour
{
    [SerializeField] private Transform headSocket;
    [SerializeField] private Transform playerRoot;

    [Header("Position Smoothing (Player-Local Axes)")]
    [SerializeField] private bool smoothX;
    [SerializeField] private float smoothTimeX = 0.05f;
    [SerializeField] private bool smoothY;
    [SerializeField] private float smoothTimeY = 0.05f;
    [SerializeField] private bool smoothZ;
    [SerializeField] private float smoothTimeZ = 0.05f;

    private Vector3 _localVelocity;
    private Vector3 _smoothedLocalOffset;

    private void Start()
    {
        _smoothedLocalOffset = playerRoot.InverseTransformPoint(headSocket.position);
    }

    private void LateUpdate()
    {
        Vector3 targetLocalOffset = playerRoot.InverseTransformPoint(headSocket.position);

        float velocityX = _localVelocity.x;
        float velocityY = _localVelocity.y;
        float velocityZ = _localVelocity.z;

        float smoothedX = smoothX ? Mathf.SmoothDamp(_smoothedLocalOffset.x, targetLocalOffset.x, ref velocityX, smoothTimeX) : targetLocalOffset.x;
        float smoothedY = smoothY ? Mathf.SmoothDamp(_smoothedLocalOffset.y, targetLocalOffset.y, ref velocityY, smoothTimeY) : targetLocalOffset.y;
        float smoothedZ = smoothZ ? Mathf.SmoothDamp(_smoothedLocalOffset.z, targetLocalOffset.z, ref velocityZ, smoothTimeZ) : targetLocalOffset.z;

        _localVelocity = new Vector3(velocityX, velocityY, velocityZ);
        _smoothedLocalOffset = new Vector3(smoothedX, smoothedY, smoothedZ);

        transform.position = playerRoot.TransformPoint(_smoothedLocalOffset);
    }
}

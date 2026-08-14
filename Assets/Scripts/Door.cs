using UnityEngine;

public class Door : MonoBehaviour
{
    [SerializeField] private bool opensToLeft;
    [SerializeField] private float openAngle = 90f;
    [SerializeField] private float rotateSpeed = 180f;

    private Quaternion _closedRotation;
    private Quaternion _targetRotation;
    private bool _isOpen;

    private void Awake()
    {
        _closedRotation = transform.localRotation;
        _targetRotation = _closedRotation;
    }

    private void Update()
    {
        transform.localRotation = Quaternion.RotateTowards(transform.localRotation, _targetRotation, rotateSpeed * Time.deltaTime);
    }

    public void Toggle()
    {
        _isOpen = !_isOpen;

        if (_isOpen)
        {
            float angle = opensToLeft ? -openAngle : openAngle;
            _targetRotation = _closedRotation * Quaternion.Euler(0f, angle, 0f);
        }
        else
        {
            _targetRotation = _closedRotation;
        }
    }
}

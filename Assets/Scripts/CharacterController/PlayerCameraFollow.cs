using UnityEngine;

[DefaultExecutionOrder(100)]
public class PlayerCameraFollow : MonoBehaviour
{
    [SerializeField] private Transform headSocket;

    private void LateUpdate()
    {
        transform.position = headSocket.position;
    }
}

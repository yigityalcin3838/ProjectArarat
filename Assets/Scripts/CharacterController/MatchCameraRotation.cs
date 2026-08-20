using UnityEngine;

// Continuously matches this transform's rotation to the camera's, regardless of
// what it's parented under (e.g. HeadSocket, which carries its own animated
// rotation from the head bone) -- so the weapon always points exactly where the
// camera is looking, on top of whatever position it inherits from its parent.
//
// Runs after every other default-order LateUpdate (PlayerAnimator is 50, and
// directly rotates bones like Spine there via ApplyPeek) so nothing touches the
// parent chain after this sets the final rotation -- otherwise this object's
// world rotation gets pulled along with whatever the parent does next, popping
// back and forth every frame and reading as jitter (this is what fixed the
// vertical trembling).
[DefaultExecutionOrder(1000)]
public class MatchCameraRotation : MonoBehaviour
{
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private bool matchPitch = true;
    [SerializeField] private bool matchYaw = true;
    [SerializeField] private bool matchRoll = true;
    private void LateUpdate()
    {
        if (cameraTransform == null)
            return;

        Vector3 cameraEuler = cameraTransform.eulerAngles;
        Vector3 currentEuler = transform.eulerAngles;

        transform.eulerAngles = new Vector3(
            matchPitch ? cameraEuler.x : currentEuler.x,
            matchYaw ? cameraEuler.y : currentEuler.y,
            matchRoll ? cameraEuler.z : currentEuler.z);
    }
}

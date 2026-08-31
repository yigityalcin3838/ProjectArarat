using UnityEngine;

// Continuously matches this transform's rotation to the camera's, regardless of
// what it's parented under (e.g. HeadSocket, which carries its own animated
// rotation from the head bone) -- so the weapon always points exactly where the
// camera is looking, on top of whatever position it inherits from its parent.
//
// In LateUpdate, and late in it, for two reasons. The animation update poses the
// parent bone first, so anything set before that gets dragged along with it and
// pops back and forth every frame (this ordering is what fixed the vertical
// trembling). And the camera being read is driven by CinemachineBrain, whose own
// LateUpdate carries no execution order and therefore runs at 0 -- reading it any
// earlier hands this object a rotation one frame out of date.
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

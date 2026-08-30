using UnityEngine;
using UnityEngine.Serialization;

// Base for anything the player can hold. What an item actually does lives in the
// derived class (see Pistol.cs); what's the same for every item lives here: while
// it's equipped, the camera watches that item's own head socket instead of the
// character's default one.
//
// The socket is a plain static child of the item's hand grip, authored per item
// -- nothing is reparented at runtime. The camera follows it because the
// Cinemachine camera is hard-locked to whatever its Tracking Target is, and
// CinemachineHardLockToTarget is a Body-stage component, so it takes position
// from there and nothing else (rotation stays with the vcam's own transform,
// which PlayerLook drives). Swapping that target therefore puts the camera on
// the grip without touching look control at all.
public class Item : MonoBehaviour
{
    [Header("Sockets")]
    [SerializeField] protected Transform handGrip;

    // This item's own head socket: put it under handGrip, positioned where the
    // eyes should sit while the item is held. Left empty, the camera just stays
    // on the character's default socket for this item.
    [FormerlySerializedAs("headSocket")]
    [SerializeField] private Transform itemHeadSocket;

    [Header("Camera")]
    [SerializeField] protected PlayerLook playerLook;

    // Reparenting is invalid while Unity tears the hierarchy down (exiting Play
    // Mode, quitting) and throws -- and OnDisable fires as part of exactly that.
    // Used by derived items, which do reparent themselves.
    protected static bool IsApplicationQuitting { get; private set; }

    private void OnApplicationQuit() => IsApplicationQuitting = true;

    protected virtual void OnEnable()
    {
        if (itemHeadSocket != null)
            playerLook?.SetCameraTargetOverride(itemHeadSocket);
    }

    protected virtual void OnDisable() => playerLook?.ClearCameraTargetOverride();
}

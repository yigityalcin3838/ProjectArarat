using UnityEngine;

// Base for anything the player can hold. What an item actually does lives in the
// derived class (see Pistol.cs); this holds the couple of things every item needs
// to be carried at all: where it sits while equipped, and the look component items
// push their camera effects into.
public class Item : MonoBehaviour
{
    [Header("Sockets")]
    // Under the camera pivot, not on the hand. An equipped item is rigid relative
    // to the view -- that is what keeps it still on screen, and it is the same
    // reason the camera is not on a bone either (see PlayerLook.cameraPivot).
    //
    // The hands are what close the gap: the aim rig turns the torso toward where
    // the camera is looking and hand IK reaches for this item's grip points, so
    // the body still visibly holds the thing and still casts a shadow doing it --
    // it just isn't the one carrying it.
    [SerializeField] protected Transform itemHold;

    [Header("Camera")]
    [SerializeField] protected PlayerLook playerLook;

    // True while the item has been unequipped but is still visibly being put
    // away. Unequipping is instant; the animation that sells it is not, and
    // anything that needs both hands free has to wait for the second one.
    public virtual bool IsStowing => false;

    // Reparenting is invalid while Unity tears the hierarchy down (exiting Play
    // Mode, quitting) and throws -- and OnDisable fires as part of exactly that.
    // Used by derived items, which do reparent themselves.
    protected static bool IsApplicationQuitting { get; private set; }

    private void OnApplicationQuit() => IsApplicationQuitting = true;
}

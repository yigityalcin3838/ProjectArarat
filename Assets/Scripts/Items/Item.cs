using UnityEngine;

// Base for anything the player can hold. What an item actually does lives in the
// derived class (see Pistol.cs); this holds the couple of things every item needs
// to be carried at all: where it sits in the hand, and the look component items
// push their camera effects into.
//
// The camera has one head socket for the whole character -- items do not move it.
public class Item : MonoBehaviour
{
    [Header("Sockets")]
    [SerializeField] protected Transform handGrip;

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

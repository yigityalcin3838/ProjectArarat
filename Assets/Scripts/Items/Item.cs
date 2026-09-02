using UnityEngine;

// Base for anything the player can hold. What an item actually does lives in the
// derived class (see Weapon.cs); this holds the couple of things every item needs
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

    // The other half: equipped, but still being brought up. Same reasoning in
    // reverse -- the slot is filled the instant the key is pressed and the hands
    // are not, and a swap started here would cut the draw in two.
    public virtual bool IsDrawing => false;

    // Either animation in flight. What anything asking "can this be interrupted"
    // actually wants, so it doesn't have to know there are two of them.
    public bool IsChangingHands => IsStowing || IsDrawing;

    // Mid-reload, for items that have such a thing. Kept apart from the two above
    // because it answers a different question: those are about the item arriving or
    // leaving, this is about it being in pieces while it stays.
    public virtual bool IsReloading => false;

    // True only inside ResetToUnequipped, so an item being put into its starting
    // state can tell that apart from being put away. Everything about unequipping is
    // written to be seen -- a holster clip, hand IK held across it, the item pose
    // kept until it finishes -- and none of that applies to a scene that has not
    // started yet.
    protected bool IsResetting { get; private set; }

    // Called once by PlayerItems on wake, for every slot, equipped or not.
    //
    // An Item left ticked in the Inspector is an authoring convenience -- it is how
    // you see the thing in the hand while placing grip points -- but Unity takes it
    // literally and runs the full OnEnable at load: the item goes to the hand, the
    // draw clip is triggered, timers start. PlayerItems then disables it a moment
    // later and the whole unequip plays over the top. The item is visible for a beat
    // and puts itself away, having never been drawn.
    //
    // So the starting state is set here rather than assumed, and the flag is what
    // lets the item skip the performance for it.
    public void ResetToUnequipped()
    {
        IsResetting = true;
        enabled = false;
        IsResetting = false;
    }

    // Reparenting is invalid while Unity tears the hierarchy down (exiting Play
    // Mode, quitting) and throws -- and OnDisable fires as part of exactly that.
    // Used by derived items, which do reparent themselves.
    protected static bool IsApplicationQuitting { get; private set; }

    private void OnApplicationQuit() => IsApplicationQuitting = true;
}

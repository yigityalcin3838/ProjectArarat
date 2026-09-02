using UnityEngine;

// Carries animation events from the weapon's model up to the Weapon component.
//
// An animation event is delivered to the GameObject that owns the Animator, and
// only that one -- Unity looks through its components for a method by name and does
// not search parents or children. The Animator is on the model, and Weapon is a
// level above it, because Weapon is the thing that gets reparented between the hand
// and the holster while the model hangs underneath it unchanged. The two can never
// be the same object, so an event has no way of reaching Weapon on its own.
//
// Hence this: it sits with the Animator, is found by the event, and passes the call
// on. One per weapon, on the same GameObject as the Animator.
public class WeaponAnimationEvents : MonoBehaviour
{
    // Filled in automatically from the parent, and left serialized only so an
    // unusual hierarchy can override it. Enabled state is irrelevant to the search
    // -- an unequipped weapon's component is disabled, not missing.
    [SerializeField] private Weapon weapon;

    private void Awake()
    {
        if (weapon == null)
            weapon = GetComponentInParent<Weapon>(includeInactive: true);
    }

    // The name typed into the Animation window's event field. Kept identical to the
    // method it forwards to, so there is only ever one name to remember.
    public void EmitShell() => weapon?.EmitShell();
}

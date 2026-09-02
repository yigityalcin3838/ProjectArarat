using UnityEngine;
using UnityEngine.InputSystem;

// Pure inventory plumbing: which item slot is active and switching between them
// on 1/2/3. Knows nothing about what an item actually does -- that lives entirely
// in the item's own Item-derived component (see Item.cs / Weapon.cs), which reacts
// to being equipped/unequipped through Unity's normal OnEnable/OnDisable. Toggling
// the Item component's `enabled` (not the GameObject's active state) so an item can
// stay visible in the scene the whole time and just reposition itself (e.g. a
// holstered pistol) instead of being forced to disappear when not equipped.
public class PlayerItems : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private InputActionAsset inputActions;

    [Header("Movement Link")]
    [SerializeField] private PlayerMovement movement;

    // Told when a swap is asked for, so the view registers the command. Only the
    // camera's half: the weapon is about to be put away and another brought up by
    // their own clips, and a spring on the hands as well would be arguing with them
    // about where the item is.
    [SerializeField] private HandMotion handMotion;

    [Header("Item Slots (1, 2, 3)")]
    [SerializeField] private GameObject[] itemSlots = new GameObject[3];

    public bool HasEquippedItem => _equippedSlot >= 0;

    // Both hands count as busy until the last frame of putting something away,
    // not just until the slot is cleared -- a ladder rung reached for with a
    // pistol still visibly in hand is the thing this exists to prevent.
    public bool AreHandsBusy => HasEquippedItem || IsAnyItemChangingHands;

    // A swap in progress: either an animation is running or one is queued behind
    // the one that is. What anything wanting to interrupt should ask about --
    // pressing a slot key or reaching for a ladder mid-draw would leave the take
    // half-played and the hands somewhere between two items.
    public bool IsChangingItem => _hasPendingSlot || IsAnyItemChangingHands;

    private bool IsAnyItemChangingHands
    {
        get
        {
            foreach (GameObject slot in itemSlots)
            {
                if (slot == null)
                    continue;

                Item item = slot.GetComponent<Item>();
                if (item != null && item.IsChangingHands)
                    return true;
            }

            return false;
        }
    }

    // Only the equipped one can be mid-reload, and only it should block a swap. Kept
    // out of IsChangingItem, which is about the hands being between two items --
    // ladders and cars stow whatever is held and are entitled to interrupt a reload
    // to do it, where reaching for another weapon is not.
    private bool IsEquippedItemReloading
    {
        get
        {
            if (_equippedSlot < 0 || _equippedSlot >= itemSlots.Length || itemSlots[_equippedSlot] == null)
                return false;

            Item item = itemSlots[_equippedSlot].GetComponent<Item>();

            return item != null && item.IsReloading;
        }
    }

    // For anything that needs the hands free before it can start. Safe to call
    // with nothing equipped. Drops a queued draw as well: whatever wants the hands
    // free wants them to stay that way, not to fill again the moment they are.
    public void StowEquippedItem()
    {
        _hasPendingSlot = false;

        if (_equippedSlot >= 0)
            SetEquippedSlot(-1);
    }

    private readonly InputAction[] _selectItemActions = new InputAction[3];
    private int _equippedSlot = -1;
    private int _pendingSlot = -1;
    private bool _hasPendingSlot;

    private void Awake()
    {
        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        for (int i = 0; i < _selectItemActions.Length; i++)
            _selectItemActions[i] = playerMap.FindAction($"SelectItem{i + 1}", throwIfNotFound: true);

        // Not SetSlotEnabled: that is the unequip, and nothing here has been equipped
        // yet. See Item.ResetToUnequipped for what the difference costs.
        foreach (GameObject slot in itemSlots)
        {
            if (slot == null)
                continue;

            Item item = slot.GetComponent<Item>();
            if (item != null)
                item.ResetToUnequipped();
        }
    }

    private void OnEnable()
    {
        foreach (InputAction action in _selectItemActions)
            action.Enable();
    }

    private void OnDisable()
    {
        foreach (InputAction action in _selectItemActions)
            action.Disable();
    }

    private void Update()
    {
        bool isBlocked = movement != null && (movement.IsInCar || movement.IsClimbingLadder);
        if (isBlocked)
        {
            // Entering a ladder or car takes both hands -- holster whatever's
            // equipped instead of leaving it stuck in the player's hand.
            StowEquippedItem();
            return;
        }

        UpdatePendingSlot();

        // Nothing new while the hands are mid-swap. A second key press here would
        // start a take over a holster that hasn't finished, and the two animations
        // would play across each other with the item ending up wherever the last
        // one left it.
        //
        // A reload holds the slot for the same reason it holds the trigger: the
        // weapon is open with a hand off the grip, and holstering out of that would
        // put it away in pieces.
        if (IsChangingItem || IsEquippedItemReloading)
            return;

        for (int i = 0; i < _selectItemActions.Length; i++)
        {
            if (!_selectItemActions[i].WasPerformedThisFrame())
                continue;

            // Empty slot -> no-op, currently equipped item stays as-is.
            if (i >= itemSlots.Length || itemSlots[i] == null)
                continue;

            // Pressing the already-equipped slot's key again holsters it instead.
            RequestSlot(i == _equippedSlot ? -1 : i);
            break;
        }
    }

    // A swap is two animations in sequence, not one exchange. Whatever is in hand
    // goes away first and the new item waits its turn -- both at once is a hand
    // holding two things and neither animation finishing on the item it belongs to.
    private void RequestSlot(int slot)
    {
        if (slot == _equippedSlot)
            return;

        // On the command, not on either animation reaching anywhere. What this marks
        // is the decision -- a stow and a draw both follow, each with its own clip,
        // and the view acknowledging the moment the key went down is what keeps the
        // whole sequence from starting silently.
        handMotion?.TriggerCameraShouldering();

        if (_equippedSlot >= 0)
        {
            SetEquippedSlot(-1);

            // Only queued when there is something to draw. Holstering on its own is
            // the whole request, not the first half of one.
            _pendingSlot = slot;
            _hasPendingSlot = slot >= 0;
            return;
        }

        SetEquippedSlot(slot);
    }

    // Deliberately only from Update, never LateUpdate. Equipping after the animation
    // update for the frame means the new item is placed in the hand and its draw
    // trigger set, but nothing evaluates either until the next frame -- so it
    // renders once in whatever pose the weapon animator was left in, fully raised,
    // before the draw has started. A frame of the weapon simply appearing.
    //
    // The frame this costs at the other end -- the outgoing item already on the hip
    // and the new one not yet up -- is covered instead by the item pose and the hand
    // IK both being held across the swap, so there is nothing to see in it.
    private void UpdatePendingSlot()
    {
        if (!_hasPendingSlot || IsAnyItemChangingHands)
            return;

        _hasPendingSlot = false;
        SetEquippedSlot(_pendingSlot);
    }

    private void SetEquippedSlot(int slot)
    {
        if (_equippedSlot >= 0 && _equippedSlot < itemSlots.Length)
            SetSlotEnabled(itemSlots[_equippedSlot], false);

        if (slot >= 0 && slot < itemSlots.Length)
            SetSlotEnabled(itemSlots[slot], true);

        _equippedSlot = slot;
    }

    private static void SetSlotEnabled(GameObject slot, bool isEnabled)
    {
        if (slot == null)
            return;

        Item item = slot.GetComponent<Item>();
        if (item != null)
            item.enabled = isEnabled;
    }
}

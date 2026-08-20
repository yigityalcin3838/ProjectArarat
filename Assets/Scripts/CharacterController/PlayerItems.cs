using UnityEngine;
using UnityEngine.InputSystem;

// Pure inventory plumbing: which item slot is active and switching between them
// on 1/2/3. Knows nothing about what an item actually does -- that lives entirely
// in the item's own Item-derived component (see Item.cs / Pistol.cs), which reacts
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

    [Header("Item Slots (1, 2, 3)")]
    [SerializeField] private GameObject[] itemSlots = new GameObject[3];

    public bool HasEquippedItem => _equippedSlot >= 0;

    private readonly InputAction[] _selectItemActions = new InputAction[3];
    private int _equippedSlot = -1;

    private void Awake()
    {
        var playerMap = inputActions.FindActionMap("Player", throwIfNotFound: true);
        for (int i = 0; i < _selectItemActions.Length; i++)
            _selectItemActions[i] = playerMap.FindAction($"SelectItem{i + 1}", throwIfNotFound: true);

        foreach (GameObject slot in itemSlots)
            SetSlotEnabled(slot, false);
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
            if (_equippedSlot >= 0)
                SetEquippedSlot(-1);
            return;
        }

        for (int i = 0; i < _selectItemActions.Length; i++)
        {
            if (!_selectItemActions[i].WasPerformedThisFrame())
                continue;

            // Empty slot -> no-op, currently equipped item stays as-is.
            if (i >= itemSlots.Length || itemSlots[i] == null)
                continue;

            // Pressing the already-equipped slot's key again holsters it instead.
            SetEquippedSlot(i == _equippedSlot ? -1 : i);
            break;
        }
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

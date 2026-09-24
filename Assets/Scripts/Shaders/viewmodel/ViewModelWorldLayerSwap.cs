using UnityEngine;

// Takes something off the view model layer and puts it back in the world for as long as
// a body action lasts, then hands it back.
//
// THE ARMS ARE THE CASE THIS EXISTS FOR. They are a separate mesh on the view model
// layer, which is right while they are holding something up in front of the eye: they
// get the near lens, they are ignored by gameplay casts, and they cannot bury themselves
// in a wall. Every one of those turns into a fault the moment the hands are doing
// something in the world instead. On a ladder or in a car they grip a rung or a wheel,
// and then:
//
//   -- the lens is wrong. That pass draws at its own narrower field of view, so the
//      forearms are at a different projection from the ladder they are holding. They
//      read as too close and too wide next to it, because they are on a different lens.
//   -- the depth is wrong, and worse. That pass CLEARS depth, so the fingers draw over
//      the rung instead of wrapping behind it. A hand cannot grip something it is
//      painted on top of.
//
// Back on the parent's layer, both stop being questions: same projection as the thing
// being held, same depth buffer, occluded by it exactly as the geometry says.
//
// THE PARENT'S LAYER RATHER THAN A FIELD, and that is the point of the design. The arms
// hang off the character, so the character's layer is by definition the one the rest of
// the body is drawn on -- which is the layer the arms have to join to share its
// projection. Naming a layer here instead would be the same fact written in two places,
// and the copy on this component is the one that would be missed when the character
// moved layers.
//
// A body action is not a moment to be doing a GetComponentsInChildren, so the hierarchy
// and the layers it was authored with are cached once, the way Item does it.
[DefaultExecutionOrder(60)]
public class ViewModelWorldLayerSwap : MonoBehaviour
{
    // Who to ask whether a body action is running. IsInBodyAction is deliberately the
    // arrival-gated flag: over the slide to the ladder the character is still walking
    // and the arms are still a view model, so the swap waits for the hands to actually
    // reach the rungs.
    [SerializeField] private PlayerMovement movement;

    // Whose layer to borrow. Left empty this object's own parent is used, which is the
    // answer that needs no setting up; point it at the character root if the arms are
    // nested under something that is itself on a layer of its own.
    [SerializeField] private Transform layerSource;

    private Transform[] _hierarchy;
    private int[] _authoredLayers;
    private bool _isInWorld;

    private void Awake()
    {
        // Inactive children included, for the same reason Item includes them: a part
        // switched off during the swap and back on afterwards would otherwise come back
        // wearing whichever layer it happened to be wearing when it left.
        _hierarchy = GetComponentsInChildren<Transform>(includeInactive: true);
        _authoredLayers = new int[_hierarchy.Length];

        for (int i = 0; i < _hierarchy.Length; i++)
            _authoredLayers[i] = _hierarchy[i].gameObject.layer;

        if (movement == null)
        {
            Debug.LogWarning(
                $"{name} has no Player Movement assigned, so its layer will never be swapped -- " +
                "the arms will stay on the view model layer through a ladder and a car, at the " +
                "wrong projection and drawn over whatever they are gripping.", this);
        }
    }

    // LateUpdate, and after the Animator: nothing here depends on the pose, but a swap
    // that lands mid-frame would have the arms drawn by one pass and occluded by the
    // other on the same frame. Settling it once, late, means each frame is drawn one way
    // or the other.
    private void LateUpdate()
    {
        bool shouldBeInWorld = movement != null && movement.IsInBodyAction;

        // Compared against what was actually applied rather than re-applied every frame.
        // It is cheap either way, but a layer write on a skinned mesh renderer is not
        // free of consequence -- it invalidates culling and batching state -- and doing
        // it sixty times a second to set the layer it already has is waste that reads as
        // intent.
        if (shouldBeInWorld == _isInWorld)
            return;

        _isInWorld = shouldBeInWorld;

        // Resolved at the moment of the swap, not cached at Awake. The character's layer
        // is not something this component gets to assume is fixed for the session, and
        // the swap is the only time the answer is needed.
        Transform source = layerSource != null ? layerSource : transform.parent;

        // Nothing to borrow from means the authored layers stand. An unparented arms mesh
        // is a setup mistake rather than a state to render, and quietly dropping it onto
        // Default would look like a rendering bug.
        if (shouldBeInWorld && source == null)
        {
            _isInWorld = false;
            return;
        }

        int worldLayer = shouldBeInWorld ? source.gameObject.layer : 0;

        for (int i = 0; i < _hierarchy.Length; i++)
        {
            if (_hierarchy[i] == null)
                continue;

            // The whole subtree onto one layer going in, and each part back onto its own
            // coming out. The two directions are not symmetrical on purpose: what they
            // share while swapped is the projection, and what they were authored with is
            // whatever each part needed.
            _hierarchy[i].gameObject.layer = shouldBeInWorld ? worldLayer : _authoredLayers[i];
        }
    }
}

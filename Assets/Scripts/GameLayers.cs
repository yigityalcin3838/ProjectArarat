using UnityEngine;

// The one place that says which layers gameplay queries are allowed to find.
//
// It exists because a physics query does not consult the Layer Collision Matrix --
// only the mask it is handed. So a thing set up to pass through everything still
// turns up in every raycast, spherecast and overlap unless each of them is told
// otherwise, and "told otherwise" spread across a weapon, a ground check, a headroom
// check and two interaction probes is five places to keep in step.
//
// Debris is the case that forced it. A severed limb lying on the floor is solid to
// the ground and to other debris and to nothing else: bullets go through it, the
// player walks through it, it is not something to stand on and not something that
// blocks a door. Every one of those is a different query, and all of them want the
// same answer.
public static class GameLayers
{
    public const string DebrisLayerName = "Debris";

    // The held item. It is drawn like anything else -- same camera, same lens, same
    // depth, same post-processing -- and this layer exists for one reason only:
    // keeping it out of the way of gameplay queries.
    //
    // Being a few centimetres from the eye puts it in front of every cast the game
    // makes. A shot would hit its own barrel, a ground check would find the stock, an
    // interaction probe would stop on the receiver. None of those is a thing that is
    // really there, so Queryable below takes this layer out.
    //
    // It used to carry a second job: held items were pulled out of the ordinary pass
    // and drawn afterwards with depth cleared, so a weapon held at arm's length would
    // not bury itself in nearby walls. That is gone. The weapon is pulled back when
    // it nears a wall, which solves the same problem where it actually arises --
    // in the weapon's position, rather than in the way the frame is assembled.
    public const string ViewModelLayerName = "ViewModel";

    private static int _debrisMask;
    private static bool _debrisResolved;
    private static int _viewModelMask;
    private static bool _viewModelResolved;

    // Everything a shot, a ground check or an interaction probe should be able to
    // find. Unity's own default raycast layers -- which already drop Ignore Raycast,
    // where a character's movement capsule lives -- minus debris and the view model.
    public static int Queryable => Physics.DefaultRaycastLayers & ~DebrisMask & ~ViewModelMask;

    public static int DebrisMask
    {
        get
        {
            if (!_debrisResolved)
            {
                int layer = LayerMask.NameToLayer(DebrisLayerName);

                // Zero, not everything, when the layer does not exist. A missing
                // layer should leave queries finding what they always found rather
                // than silently excluding some other layer that happens to be at
                // whatever index NameToLayer failed with.
                _debrisMask = layer >= 0 ? 1 << layer : 0;
                _debrisResolved = true;
            }

            return _debrisMask;
        }
    }

    public static int ViewModelMask
    {
        get
        {
            if (!_viewModelResolved)
            {
                int layer = LayerMask.NameToLayer(ViewModelLayerName);
                _viewModelMask = layer >= 0 ? 1 << layer : 0;
                _viewModelResolved = true;
            }

            return _viewModelMask;
        }
    }

    // The layer to put a held item's visible geometry on. -1 when the project has no
    // such layer, which the caller should read as "leave it where it is" rather than
    // as an index.
    public static int ViewModelLayer
    {
        get
        {
            int layer = LayerMask.NameToLayer(ViewModelLayerName);

            if (layer < 0)
            {
                Debug.LogWarning(
                    $"No layer called '{ViewModelLayerName}'. Items will be drawn in sequence with " +
                    "the world, so they will clip into walls, and will be found by shots and " +
                    "interaction probes. Add the layer under Project Settings > Tags and Layers.");
            }

            return layer;
        }
    }

    // The layer to put a severed piece on. Warns rather than failing quietly, since
    // the symptom otherwise is debris behaving like scenery -- blocking shots,
    // catching feet -- with nothing to connect it to a missing project setting.
    public static int DebrisLayer
    {
        get
        {
            int layer = LayerMask.NameToLayer(DebrisLayerName);

            if (layer < 0)
            {
                Debug.LogWarning(
                    $"No layer called '{DebrisLayerName}'. Debris will sit on Default and be " +
                    "treated as ordinary scenery -- shots will stop on it and characters will " +
                    "stand on it. Add the layer under Project Settings > Tags and Layers.");
            }

            return layer;
        }
    }
}

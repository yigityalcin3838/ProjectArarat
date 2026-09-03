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

    private static int _debrisMask;
    private static bool _resolved;

    // Everything a shot, a ground check or an interaction probe should be able to
    // find. Unity's own default raycast layers -- which already drop Ignore Raycast,
    // where a character's movement capsule lives -- minus debris.
    public static int Queryable => Physics.DefaultRaycastLayers & ~DebrisMask;

    public static int DebrisMask
    {
        get
        {
            if (!_resolved)
            {
                int layer = LayerMask.NameToLayer(DebrisLayerName);

                // Zero, not everything, when the layer does not exist. A missing
                // layer should leave queries finding what they always found rather
                // than silently excluding some other layer that happens to be at
                // whatever index NameToLayer failed with.
                _debrisMask = layer >= 0 ? 1 << layer : 0;
                _resolved = true;
            }

            return _debrisMask;
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

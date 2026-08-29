using System.Collections.Generic;
using UnityEngine;

// A region of thicker (or thinner) air -- a dusty interior, a cellar, a cave
// mouth.
//
// This exists because no single global density serves both cases: outdoors a
// view ray passes through sunlit air for its whole length, indoors only a
// thin slice of it is lit, so outdoors is genuinely brighter. A density
// strong enough to make an indoor window beam read will always wash the
// outdoors out. Unreal's Volumetric Fog documentation gives the same answer:
// keep global density low and add local volumes where the air should be
// thick.
//
// The region is the attached BoxCollider -- its centre and size, through the
// object's transform. Only its dimensions are read; it takes no part in
// physics, which is why Reset marks it a trigger so it can't block anything
// walking through.
//
// Checked per ray-march step rather than from the camera's position, which is
// what lets a beam inside a room still look right while standing in the
// doorway looking in.
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public class SunShaftsDensityVolume : MonoBehaviour
{
    // Matches SUNSHAFTS_MAX_DENSITY_VOLUMES in SunShafts.shader. Every extra
    // volume costs a box test on every ray-march step of every pixel, so this
    // is deliberately small.
    public const int MaxVolumes = 8;

    [Tooltip("Multiplies the Sun Shafts Density inside this region. Above 1 thickens the air " +
             "(interiors), below 1 thins it.")]
    [Min(0f)]
    public float densityMultiplier = 6f;

    [Tooltip("How far in from the edge the change fades, as a fraction of the box. 0 gives a " +
             "hard boundary that reads as a visible plane cutting through the air; a little " +
             "softness hides it.")]
    [Range(0.001f, 0.5f)]
    public float edgeFalloff = 0.15f;

    private static readonly List<SunShaftsDensityVolume> Active = new List<SunShaftsDensityVolume>();

    public static IReadOnlyList<SunShaftsDensityVolume> ActiveVolumes => Active;

    private BoxCollider _box;

    private void OnEnable()
    {
        _box = GetComponent<BoxCollider>();
        Active.Add(this);
    }

    private void OnDisable() => Active.Remove(this);

    private void Reset()
    {
        // Nothing here reads the collider through physics, so leaving it solid
        // would only block whatever walks into the room.
        GetComponent<BoxCollider>().isTrigger = true;
    }

    // Maps world space onto the box's own space, scaled so the box is a unit
    // cube centred on the origin -- the shader's inside-test is then just
    // "all axes within +/-0.5", with position, rotation and size all folded
    // into this one matrix.
    public bool TryGetWorldToLocal(out Matrix4x4 worldToLocal)
    {
        worldToLocal = Matrix4x4.identity;

        BoxCollider box = _box != null ? _box : GetComponent<BoxCollider>();
        if (box == null || !box.enabled)
            return false;

        Vector3 size = box.size;
        if (size.x <= 0f || size.y <= 0f || size.z <= 0f)
            return false;

        Matrix4x4 boxToWorld = transform.localToWorldMatrix * Matrix4x4.TRS(box.center, Quaternion.identity, size);
        worldToLocal = boxToWorld.inverse;
        return true;
    }
}

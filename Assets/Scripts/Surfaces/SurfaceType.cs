using UnityEngine;

// One asset per kind of surface (Stone, Glass, Metal, Grass, Snow, Gravel...).
// Everything that needs to react to "what did I hit" reads from here, so
// adding a sound or a decal later means adding a field to this one file --
// no caller has to change.
[CreateAssetMenu(fileName = "Surface", menuName = "Surfaces/Surface Type")]
public class SurfaceType : ScriptableObject
{
    [Tooltip("Spawned at the hit point, rotated to face along the surface normal. " +
             "The prefab is responsible for destroying itself (e.g. a ParticleSystem " +
             "with Stop Action set to Destroy).")]
    public GameObject impactEffectPrefab;

    // Later: FMOD event reference for the impact sound, bullet-hole decal,
    // footstep event, penetration multiplier. They belong here so they stay
    // in sync with the particle -- one asset describes the surface fully.
}

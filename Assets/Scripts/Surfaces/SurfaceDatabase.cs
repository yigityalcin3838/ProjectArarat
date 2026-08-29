using System;
using System.Collections.Generic;
using UnityEngine;

// The terrain-layer to SurfaceType lookup, as an asset so it's shared across
// every scene rather than re-created per scene. A scene points at one of
// these through SurfaceSystem.
[CreateAssetMenu(fileName = "SurfaceDatabase", menuName = "Surfaces/Surface Database")]
public class SurfaceDatabase : ScriptableObject
{
    [Serializable]
    public struct TerrainLayerEntry
    {
        public TerrainLayer terrainLayer;
        public SurfaceType surface;
    }

    [Tooltip("Used when the terrain layer that was hit isn't in the list below, so an " +
             "unmapped layer still gets an impact effect instead of nothing.")]
    public SurfaceType defaultSurface;

    public TerrainLayerEntry[] terrainLayers = Array.Empty<TerrainLayerEntry>();

    // Built on first use rather than every lookup -- a bullet impact
    // shouldn't walk the whole array. Rebuilt whenever the asset is edited
    // (OnValidate) so changes show up without re-entering play mode.
    private Dictionary<TerrainLayer, SurfaceType> _terrainLayerLookup;

    private void OnEnable() => Invalidate();
    private void OnValidate() => Invalidate();

    private void Invalidate() => _terrainLayerLookup = null;

    public SurfaceType GetForTerrainLayer(TerrainLayer layer)
    {
        if (layer == null)
            return null;

        if (_terrainLayerLookup == null)
        {
            _terrainLayerLookup = new Dictionary<TerrainLayer, SurfaceType>();
            foreach (TerrainLayerEntry entry in terrainLayers)
            {
                if (entry.terrainLayer != null)
                    _terrainLayerLookup[entry.terrainLayer] = entry.surface;
            }
        }

        return _terrainLayerLookup.TryGetValue(layer, out SurfaceType surface) ? surface : null;
    }
}

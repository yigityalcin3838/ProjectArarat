using UnityEngine;

// Put one of these on a GameObject in the scene and point it at a
// SurfaceDatabase asset. Anything that hits something asks it what was hit,
// so weapons don't each need their own copy of the mapping.
//
// Terrain is told apart properly -- it's a single collider with a single
// material but many layers blended per-pixel, so the layer weights at the
// hit point are the only thing that says what was really hit. Everything
// else (walls, props, vehicles) currently gets the default effect; telling
// those apart by material comes later and only means adding a branch here,
// without touching any caller.
[DisallowMultipleComponent]
public class SurfaceSystem : MonoBehaviour
{
    [SerializeField] private SurfaceDatabase database;

    [Tooltip("On terrain, layers blend into each other, so a hit is rarely 100% one layer. " +
             "On: pick a layer at random weighted by how much of it is there, so a gravel " +
             "patch fading into grass throws the occasional gravel hit -- matches how the " +
             "height blend actually looks. Off: always use whichever layer is strongest.")]
    [SerializeField] private bool weightedTerrainLayerPick = true;

    public static SurfaceSystem Instance { get; private set; }

    private void OnEnable()
    {
        // Last one to enable wins rather than the first -- an additively
        // loaded scene overriding the mapping is the useful behavior, and
        // it avoids a stale Instance if scenes unload out of order.
        Instance = this;
    }

    private void OnDisable()
    {
        if (Instance == this)
            Instance = null;
    }

    // Null only when there's no database, or it has no default surface set.
    public SurfaceType Resolve(RaycastHit hit)
    {
        if (hit.collider == null || database == null)
            return null;

        Terrain terrain = hit.collider.GetComponent<Terrain>();
        if (terrain != null)
        {
            SurfaceType terrainSurface = ResolveTerrain(terrain, hit.point);
            if (terrainSurface != null)
                return terrainSurface;
        }

        // Everything else -- walls, props, vehicles -- gets the default, so
        // a shot always leaves something rather than silently nothing. Those
        // surfaces get told apart properly later; this is the baseline.
        return database.defaultSurface;
    }

    // Callers go through this rather than instantiating themselves, so what
    // spawns for a given hit stays decided in one place.
    public void SpawnImpact(RaycastHit hit)
    {
        SurfaceType surface = Resolve(hit);
        if (surface == null || surface.impactEffectPrefab == null)
            return;

        Instantiate(surface.impactEffectPrefab, hit.point, Quaternion.LookRotation(hit.normal));
    }

    // The per-layer blend weights at a world point, as a 1x1 alphamap read.
    // Reads a single cell rather than caching the whole alphamap: a full
    // cache would be megabytes of RAM for a large terrain, while this is a
    // tiny short-lived array at the rate bullets actually land.
    private static bool TryGetAlphamapWeights(Terrain terrain, Vector3 worldPoint, out float[,,] weights)
    {
        weights = null;

        TerrainData data = terrain.terrainData;
        if (data == null || data.alphamapWidth <= 0 || data.alphamapHeight <= 0)
            return false;

        Vector3 local = worldPoint - terrain.transform.position;
        float normalizedX = local.x / data.size.x;
        float normalizedZ = local.z / data.size.z;

        int mapX = Mathf.Clamp(Mathf.FloorToInt(normalizedX * data.alphamapWidth), 0, data.alphamapWidth - 1);
        int mapZ = Mathf.Clamp(Mathf.FloorToInt(normalizedZ * data.alphamapHeight), 0, data.alphamapHeight - 1);

        weights = data.GetAlphamaps(mapX, mapZ, 1, 1);
        return weights != null;
    }

    private SurfaceType ResolveTerrain(Terrain terrain, Vector3 worldPoint)
    {
        TerrainData data = terrain.terrainData;
        if (data == null)
            return null;

        TerrainLayer[] layers = data.terrainLayers;
        if (layers == null || layers.Length == 0)
            return null;

        if (!TryGetAlphamapWeights(terrain, worldPoint, out float[,,] weights))
            return null;

        int layerCount = Mathf.Min(layers.Length, weights.GetLength(2));

        int chosenLayer = -1;

        if (weightedTerrainLayerPick)
        {
            float total = 0f;
            for (int i = 0; i < layerCount; i++)
                total += weights[0, 0, i];

            if (total > 0f)
            {
                float roll = Random.value * total;
                for (int i = 0; i < layerCount; i++)
                {
                    roll -= weights[0, 0, i];
                    if (roll <= 0f)
                    {
                        chosenLayer = i;
                        break;
                    }
                }
                // Guard against the roll falling past the end on rounding.
                if (chosenLayer < 0)
                    chosenLayer = layerCount - 1;
            }
        }

        if (chosenLayer < 0)
        {
            float strongest = -1f;
            for (int i = 0; i < layerCount; i++)
            {
                if (weights[0, 0, i] > strongest)
                {
                    strongest = weights[0, 0, i];
                    chosenLayer = i;
                }
            }
        }

        return chosenLayer >= 0 ? database.GetForTerrainLayer(layers[chosenLayer]) : null;
    }
}

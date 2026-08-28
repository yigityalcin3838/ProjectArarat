using UnityEngine;

// Binds every Terrain Layer's control map and Mask Map as global shader
// textures, so TerrainLitStochastic's height blend can evaluate ALL layers
// in every pass instead of only the 4 that pass happens to be drawing.
// Without this, Unity's terrain system splits layers 0-3 / 4-7 / ... across
// separate additive draw calls that can't see each other's heights, which
// is exactly why stock Unity disables height-based blend above 4 layers.
// See HeightBasedSplatModify in TerrainLitPassesStochastic.hlsl.
//
// Attach to the Terrain. Runs in edit mode too so painting updates live.
[ExecuteAlways]
[RequireComponent(typeof(Terrain))]
public class TerrainGlobalHeightBlend : MonoBehaviour
{
    // Matches GLOBAL_HEIGHT_MAX_LAYERS in TerrainLitInputStochastic.hlsl --
    // 8 layers is 2 control maps, which is what the shader declares.
    private const int MaxLayers = 8;

    private static readonly int ControlTexelSizeId = Shader.PropertyToID("_GlobalControlTexelSize");
    private static readonly int LayerCountId = Shader.PropertyToID("_GlobalLayerCount");
    private static readonly int SplatSTId = Shader.PropertyToID("_GlobalSplatST");
    private static readonly int HeightRemapId = Shader.PropertyToID("_GlobalHeightRemap");

    private static readonly int[] ControlIds =
    {
        Shader.PropertyToID("_GlobalControl0"),
        Shader.PropertyToID("_GlobalControl1"),
    };

    private static readonly int[] MaskIds =
    {
        Shader.PropertyToID("_GlobalMask0"),
        Shader.PropertyToID("_GlobalMask1"),
        Shader.PropertyToID("_GlobalMask2"),
        Shader.PropertyToID("_GlobalMask3"),
        Shader.PropertyToID("_GlobalMask4"),
        Shader.PropertyToID("_GlobalMask5"),
        Shader.PropertyToID("_GlobalMask6"),
        Shader.PropertyToID("_GlobalMask7"),
    };

    private readonly Vector4[] _splatST = new Vector4[MaxLayers];
    private readonly Vector4[] _heightRemap = new Vector4[MaxLayers];

    private Terrain _terrain;

    private void OnEnable()
    {
        _terrain = GetComponent<Terrain>();
        Apply();
    }

    // Terrain painting doesn't raise any change event, so the values are
    // refreshed every frame -- it's a handful of SetGlobal calls with no
    // texture copies, cheap enough not to be worth a dirty-tracking scheme.
    private void Update()
    {
        Apply();
    }

    private void OnValidate()
    {
        _terrain = GetComponent<Terrain>();
        Apply();
    }

    private void Apply()
    {
        if (_terrain == null || _terrain.terrainData == null)
            return;

        TerrainData data = _terrain.terrainData;
        TerrainLayer[] layers = data.terrainLayers;
        if (layers == null || layers.Length == 0)
            return;

        int layerCount = Mathf.Min(layers.Length, MaxLayers);
        Vector3 terrainSize = data.size;

        for (int i = 0; i < MaxLayers; i++)
        {
            if (i >= layerCount || layers[i] == null)
            {
                _splatST[i] = new Vector4(1f, 1f, 0f, 0f);
                _heightRemap[i] = Vector4.zero;
                Shader.SetGlobalTexture(MaskIds[i], Texture2D.blackTexture);
                continue;
            }

            TerrainLayer layer = layers[i];

            // Same relationship Unity's terrain system uses to build
            // _SplatX_ST: mesh UV (0-1 across the terrain) scaled up by
            // worldSize/tileSize so the texture repeats that many times.
            _splatST[i] = new Vector4(
                terrainSize.x / layer.tileSize.x,
                terrainSize.z / layer.tileSize.y,
                layer.tileOffset.x / layer.tileSize.x,
                layer.tileOffset.y / layer.tileSize.y);

            // TerrainLayer's remap Vector4s are (metallic, AO, height,
            // smoothness) -- height is .z, matching this project's Mask Map
            // channel convention.
            float remapMin = layer.maskMapRemapMin.z;
            float remapScale = layer.maskMapRemapMax.z - layer.maskMapRemapMin.z;
            bool hasMask = layer.maskMapTexture != null;

            _heightRemap[i] = new Vector4(remapScale, remapMin, hasMask ? 1f : 0f, 0f);
            Shader.SetGlobalTexture(MaskIds[i], hasMask ? layer.maskMapTexture : Texture2D.blackTexture);
        }

        Texture[] controlMaps = data.alphamapTextures;
        for (int i = 0; i < ControlIds.Length; i++)
        {
            Shader.SetGlobalTexture(ControlIds[i],
                i < controlMaps.Length && controlMaps[i] != null ? controlMaps[i] : Texture2D.blackTexture);
        }

        int alphaWidth = data.alphamapWidth;
        int alphaHeight = data.alphamapHeight;
        Shader.SetGlobalVector(ControlTexelSizeId,
            new Vector4(1f / alphaWidth, 1f / alphaHeight, alphaWidth, alphaHeight));

        Shader.SetGlobalFloat(LayerCountId, layerCount);
        Shader.SetGlobalVectorArray(SplatSTId, _splatST);
        Shader.SetGlobalVectorArray(HeightRemapId, _heightRemap);
    }
}

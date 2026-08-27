using System.IO;
using UnityEditor;
using UnityEngine;

// One-off tool: splits _HeightSplatAll (SplatTex.psd -- Grass=R, Cliff=G,
// Stones=B, Snow=A) into 4 separate URP Terrain Layer Mask Maps, one per
// layer, with that layer's height value written into the Mask Map's Blue
// channel (URP Mask Map convention: R=Metallic, G=AO, B=Height, A=Smoothness).
// Run via Tools > CLIFF > Generate Terrain Mask Maps.
public static class TerrainMaskMapGenerator
{
    private const string SourcePath = "Assets/CLIFF/Terrain/Textures/SplatTex.psd";
    private const string OutputFolder = "Assets/CLIFF/Terrain/Textures/MaskMaps";

    private struct LayerInfo
    {
        public string Name;
        public int SourceChannel; // 0=R, 1=G, 2=B, 3=A

        public LayerInfo(string name, int channel)
        {
            Name = name;
            SourceChannel = channel;
        }
    }

    [MenuItem("Tools/CLIFF/Generate Terrain Mask Maps")]
    private static void Generate()
    {
        var importer = AssetImporter.GetAtPath(SourcePath) as TextureImporter;
        if (importer == null)
        {
            Debug.LogError($"Could not find texture importer at {SourcePath}");
            return;
        }

        bool wasReadable = importer.isReadable;
        if (!wasReadable)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
        }

        Texture2D source = AssetDatabase.LoadAssetAtPath<Texture2D>(SourcePath);
        if (source == null)
        {
            Debug.LogError($"Could not load texture at {SourcePath}");
            return;
        }

        Color32[] pixels = source.GetPixels32();
        int width = source.width;
        int height = source.height;

        if (!Directory.Exists(OutputFolder))
            Directory.CreateDirectory(OutputFolder);

        var layers = new[]
        {
            new LayerInfo("Grass", 0),
            new LayerInfo("Cliff", 1),
            new LayerInfo("Stones", 2),
            new LayerInfo("Snow", 3),
        };

        foreach (LayerInfo layer in layers)
        {
            var maskPixels = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                byte heightValue = layer.SourceChannel switch
                {
                    0 => pixels[i].r,
                    1 => pixels[i].g,
                    2 => pixels[i].b,
                    _ => pixels[i].a,
                };

                // R=Metallic(0), G=AO(255=no extra occlusion), B=Height, A=Smoothness.
                // Low (matte) default -- natural terrain shouldn't be glossy;
                // push individual layers up via that Terrain Layer's own
                // Smoothness slider in the Inspector if a layer (e.g. wet rock,
                // icy snow) should shine more, instead of regenerating this map.
                maskPixels[i] = new Color32(0, 255, heightValue, 20);
            }

            var maskTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            maskTexture.SetPixels32(maskPixels);
            maskTexture.Apply();

            byte[] png = maskTexture.EncodeToPNG();
            string outPath = $"{OutputFolder}/{layer.Name}_Mask.png";
            File.WriteAllBytes(outPath, png);
            Object.DestroyImmediate(maskTexture);

            Debug.Log($"Wrote {outPath}");
        }

        if (!wasReadable)
        {
            importer.isReadable = false;
            importer.SaveAndReimport();
        }

        AssetDatabase.Refresh();

        // Mask Maps must be imported as linear (not sRGB) and shouldn't be
        // compressed lossily, since the Blue channel carries precise height
        // data, not color.
        foreach (LayerInfo layer in layers)
        {
            string outPath = $"{OutputFolder}/{layer.Name}_Mask.png";
            var maskImporter = AssetImporter.GetAtPath(outPath) as TextureImporter;
            if (maskImporter == null)
                continue;

            maskImporter.sRGBTexture = false;
            maskImporter.textureType = TextureImporterType.Default;
            maskImporter.SaveAndReimport();
        }

        Debug.Log("Done. Assign each <Layer>_Mask.png as that Terrain Layer's Mask Map, then enable Height-Based Blend in the Terrain's Textures settings.");
    }
}

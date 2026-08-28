using System.IO;
using UnityEditor;
using UnityEngine;

// Packs four separate PBR maps into one URP Mask Map
// (R=Metallic, G=AO, B=Height, A=Smoothness) -- the layout every terrain
// layer and this project's terrain shaders expect. Height goes in Blue,
// which is what TerrainLitStochastic's height blend reads.
//
// Unlike TerrainMaskMapGenerator (which splits one pre-combined CLIFF splat
// texture), this takes the loose per-channel maps a typical downloaded
// texture set ships with: AO, Displacement, Gloss/Roughness, etc.
//
// Window > CLIFF > Mask Map Packer.
public class MaskMapPackerWindow : EditorWindow
{
    private Texture2D _metallic;
    private Texture2D _ao;
    private Texture2D _height;
    private Texture2D _smoothness;

    private bool _invertSmoothness = true;
    private float _metallicFallback;
    private float _aoFallback = 1f;
    private float _heightFallback = 0.5f;
    private float _smoothnessFallback = 0.5f;

    private string _outputName = "NewMaskMap";
    private string _outputFolder = "Assets/CLIFF/Terrain/Textures/MaskMaps";

    [MenuItem("Window/CLIFF/Mask Map Packer")]
    private static void Open()
    {
        GetWindow<MaskMapPackerWindow>("Mask Map Packer");
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Packs into a URP Mask Map: R=Metallic, G=AO, B=Height, A=Smoothness.\n" +
            "Height (Blue) is what the terrain height blend reads -- use the set's Displacement map for it.",
            MessageType.Info);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Source Maps", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Leave any slot empty to fill that channel with its constant below.", EditorStyles.miniLabel);

        _metallic = (Texture2D)EditorGUILayout.ObjectField("R  Metallic", _metallic, typeof(Texture2D), false);
        if (_metallic == null)
            _metallicFallback = EditorGUILayout.Slider("      Constant", _metallicFallback, 0f, 1f);

        _ao = (Texture2D)EditorGUILayout.ObjectField("G  Ambient Occlusion", _ao, typeof(Texture2D), false);
        if (_ao == null)
            _aoFallback = EditorGUILayout.Slider("      Constant", _aoFallback, 0f, 1f);

        _height = (Texture2D)EditorGUILayout.ObjectField("B  Height / Displacement", _height, typeof(Texture2D), false);
        if (_height == null)
            _heightFallback = EditorGUILayout.Slider("      Constant", _heightFallback, 0f, 1f);

        _smoothness = (Texture2D)EditorGUILayout.ObjectField("A  Smoothness / Gloss", _smoothness, typeof(Texture2D), false);
        if (_smoothness == null)
            _smoothnessFallback = EditorGUILayout.Slider("      Constant", _smoothnessFallback, 0f, 1f);
        else
            _invertSmoothness = EditorGUILayout.Toggle("      Invert (it's Roughness)", _invertSmoothness);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        _outputName = EditorGUILayout.TextField("File Name", _outputName);
        _outputFolder = EditorGUILayout.TextField("Folder", _outputFolder);

        EditorGUILayout.Space();

        // Every source has to agree on resolution -- the packer reads them
        // texel for texel rather than resampling, so a mismatch would pair
        // up unrelated parts of the surface.
        Texture2D reference = _metallic ?? _ao ?? _height ?? _smoothness;
        if (reference == null)
        {
            EditorGUILayout.HelpBox("Assign at least one source map.", MessageType.Warning);
            return;
        }

        string sizeMismatch = FindSizeMismatch(reference);
        if (sizeMismatch != null)
        {
            EditorGUILayout.HelpBox(sizeMismatch, MessageType.Error);
            return;
        }

        if (GUILayout.Button($"Pack {reference.width}x{reference.height} Mask Map"))
            Pack(reference.width, reference.height);
    }

    private string FindSizeMismatch(Texture2D reference)
    {
        Texture2D[] sources = { _metallic, _ao, _height, _smoothness };
        string[] names = { "Metallic", "AO", "Height", "Smoothness" };

        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i] == null)
                continue;
            if (sources[i].width != reference.width || sources[i].height != reference.height)
            {
                return $"{names[i]} is {sources[i].width}x{sources[i].height} but the others are " +
                       $"{reference.width}x{reference.height}. All source maps must match.";
            }
        }
        return null;
    }

    private void Pack(int width, int height)
    {
        // Reading pixels needs Read/Write enabled; these are normally
        // imported without it, so it's flipped on temporarily and restored
        // afterwards rather than left changed behind the user's back.
        var restore = new System.Collections.Generic.List<TextureImporter>();

        Color32[] metallicPixels = ReadPixels(_metallic, restore);
        Color32[] aoPixels = ReadPixels(_ao, restore);
        Color32[] heightPixels = ReadPixels(_height, restore);
        Color32[] smoothnessPixels = ReadPixels(_smoothness, restore);

        byte metallicConst = (byte)Mathf.RoundToInt(_metallicFallback * 255f);
        byte aoConst = (byte)Mathf.RoundToInt(_aoFallback * 255f);
        byte heightConst = (byte)Mathf.RoundToInt(_heightFallback * 255f);
        byte smoothnessConst = (byte)Mathf.RoundToInt(_smoothnessFallback * 255f);

        var packed = new Color32[width * height];
        for (int i = 0; i < packed.Length; i++)
        {
            byte r = metallicPixels != null ? metallicPixels[i].r : metallicConst;
            byte g = aoPixels != null ? aoPixels[i].r : aoConst;
            byte b = heightPixels != null ? heightPixels[i].r : heightConst;

            byte a;
            if (smoothnessPixels != null)
                a = _invertSmoothness ? (byte)(255 - smoothnessPixels[i].r) : smoothnessPixels[i].r;
            else
                a = smoothnessConst;

            packed[i] = new Color32(r, g, b, a);
        }

        foreach (TextureImporter importer in restore)
        {
            importer.isReadable = false;
            importer.SaveAndReimport();
        }

        if (!Directory.Exists(_outputFolder))
            Directory.CreateDirectory(_outputFolder);

        var maskTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        maskTexture.SetPixels32(packed);
        maskTexture.Apply();

        string outputPath = $"{_outputFolder}/{_outputName}.png";
        File.WriteAllBytes(outputPath, maskTexture.EncodeToPNG());
        DestroyImmediate(maskTexture);

        AssetDatabase.ImportAsset(outputPath);

        // Mask Maps carry data, not color -- sRGB would gamma-curve the
        // height values and shift where layers blend.
        var maskImporter = AssetImporter.GetAtPath(outputPath) as TextureImporter;
        if (maskImporter != null)
        {
            maskImporter.sRGBTexture = false;
            maskImporter.textureType = TextureImporterType.Default;
            maskImporter.alphaSource = TextureImporterAlphaSource.FromInput;
            maskImporter.alphaIsTransparency = false;
            maskImporter.SaveAndReimport();
        }

        Texture2D result = AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath);
        EditorGUIUtility.PingObject(result);
        Selection.activeObject = result;

        Debug.Log($"Packed Mask Map written to {outputPath}. Assign it as the Terrain Layer's Mask Map.");
    }

    private static Color32[] ReadPixels(Texture2D texture, System.Collections.Generic.List<TextureImporter> restore)
    {
        if (texture == null)
            return null;

        string path = AssetDatabase.GetAssetPath(texture);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null && !importer.isReadable)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
            restore.Add(importer);
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path).GetPixels32();
    }
}

using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Unity's own Terrain Lit inspector (UnityEditor.Rendering.Universal.TerrainLitShaderGUI)
// is `internal`, so it can't be inherited from outside the URP editor assembly.
// Instead, this finds it via reflection, creates an instance, and calls its
// OnGUI through the public ShaderGUI base type -- that's allowed even though
// the concrete type itself isn't accessible by name here -- so the original
// Terrain inspector (Paint Texture, Terrain Layer list, Height Transition,
// everything) still renders exactly as it does with the stock shader, and
// this just appends the per-layer tint fields underneath it.
public class TerrainLitStochasticShaderGUI : ShaderGUI
{
    private const string InnerTypeName = "UnityEditor.Rendering.Universal.TerrainLitShaderGUI";

    private static readonly string[] TintPropertyNames = { "_TintColor0", "_TintColor1", "_TintColor2", "_TintColor3" };
    private static readonly string[] TintBPropertyNames = { "_TintColor0B", "_TintColor1B", "_TintColor2B", "_TintColor3B" };
    private static readonly string[] VariationScalePropertyNames = { "_VariationScale0", "_VariationScale1", "_VariationScale2", "_VariationScale3" };
    private static readonly string[] ParallaxScalePropertyNames = { "_ParallaxScale0", "_ParallaxScale1", "_ParallaxScale2", "_ParallaxScale3" };
    private static readonly string[] LayerLabels = { "Layer 0", "Layer 1", "Layer 2", "Layer 3" };

    private ShaderGUI _innerGUI;
    private bool _lookedUpInnerGUI;

    private ShaderGUI GetInnerGUI()
    {
        if (_lookedUpInnerGUI)
            return _innerGUI;

        _lookedUpInnerGUI = true;

        Type innerType = null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            innerType = assembly.GetType(InnerTypeName);
            if (innerType != null)
                break;
        }

        if (innerType == null)
        {
            Debug.LogError($"{InnerTypeName} not found -- falling back to the default material inspector.");
            return null;
        }

        _innerGUI = Activator.CreateInstance(innerType) as ShaderGUI;
        return _innerGUI;
    }

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        ShaderGUI inner = GetInnerGUI();
        if (inner != null)
            inner.OnGUI(materialEditor, properties);
        else
            base.OnGUI(materialEditor, properties);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Stochastic Terrain -- Layer Tints", EditorStyles.boldLabel);

        for (int i = 0; i < TintPropertyNames.Length; i++)
        {
            MaterialProperty tint = FindProperty(TintPropertyNames[i], properties, false);
            MaterialProperty tintB = FindProperty(TintBPropertyNames[i], properties, false);
            MaterialProperty variationScale = FindProperty(VariationScalePropertyNames[i], properties, false);
            MaterialProperty parallaxScale = FindProperty(ParallaxScalePropertyNames[i], properties, false);
            if (tint == null || tintB == null || variationScale == null || parallaxScale == null)
                continue;

            EditorGUILayout.LabelField(LayerLabels[i], EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(tint, "Tint");
            materialEditor.ShaderProperty(tintB, "Tint Variation");
            materialEditor.ShaderProperty(variationScale, "Variation Scale");
            materialEditor.ShaderProperty(parallaxScale, "Parallax Scale");
            EditorGUI.indentLevel--;
        }
    }
}

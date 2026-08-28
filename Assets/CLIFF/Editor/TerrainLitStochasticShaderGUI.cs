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

    // 8 layers: Unity draws 0-3 in the base pass and 4-7 in the Add Pass,
    // and each needs its own settings (see the CBUFFER comment in
    // TerrainLitInputStochastic.hlsl).
    private const int MaxLayers = 8;

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

        for (int i = 0; i < MaxLayers; i++)
        {
            MaterialProperty tint = FindProperty($"_TintColor{i}", properties, false);
            MaterialProperty tintB = FindProperty($"_TintColor{i}B", properties, false);
            MaterialProperty variationScale = FindProperty($"_VariationScale{i}", properties, false);
            MaterialProperty parallaxScale = FindProperty($"_ParallaxScale{i}", properties, false);
            if (tint == null || tintB == null || variationScale == null || parallaxScale == null)
                continue;

            EditorGUILayout.LabelField($"Layer {i}", EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(tint, "Tint");
            materialEditor.ShaderProperty(tintB, "Tint Variation");
            materialEditor.ShaderProperty(variationScale, "Variation Scale");
            materialEditor.ShaderProperty(parallaxScale, "Parallax Scale");
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Stochastic Terrain -- Height Blend (>4 layers)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "For Height Transition to work with more than 4 Terrain Layers, the Terrain needs a TerrainGlobalHeightBlend component -- it binds every layer's mask so each render pass can blend against all of them, not just its own 4. Nothing to bake; it updates live as you paint.",
            MessageType.Info);
    }
}

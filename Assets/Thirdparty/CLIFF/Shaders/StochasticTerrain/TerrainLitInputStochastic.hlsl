// Copy of URP's TerrainLitInput.hlsl (com.unity.render-pipelines.universal
// Shaders/Terrain/TerrainLitInput.hlsl) with stochastic-tiling sampling
// utilities appended at the bottom. Everything above the "STOCHASTIC TILING"
// section is Unity's own unmodified terrain input code, kept identical on
// purpose so every existing Terrain feature (masks, holes, instancing, all of
// it) keeps working exactly as it does with the stock shader.
#ifndef UNIVERSAL_TERRAIN_LIT_INPUT_STOCHASTIC_INCLUDED
#define UNIVERSAL_TERRAIN_LIT_INPUT_STOCHASTIC_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/DebugMipmapStreamingMacros.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _MainTex_ST;
    half4 _BaseColor;
    half _Cutoff;

    UNITY_TEXTURE_STREAMING_DEBUG_VARS_FOR_TEX(_Control);
    float4 _Splat0_TexelSize, _Splat1_TexelSize, _Splat2_TexelSize, _Splat3_TexelSize;
    UNITY_TEXTURE_STREAMING_DEBUG_VARS_FOR_TEX(_Splat0);
    UNITY_TEXTURE_STREAMING_DEBUG_VARS_FOR_TEX(_Splat1);
    UNITY_TEXTURE_STREAMING_DEBUG_VARS_FOR_TEX(_Splat2);
    UNITY_TEXTURE_STREAMING_DEBUG_VARS_FOR_TEX(_Splat3);
CBUFFER_END

#define _Surface 0.0 // Terrain is always opaque

CBUFFER_START(_Terrain)
    half _NormalScale0, _NormalScale1, _NormalScale2, _NormalScale3;
    half _Metallic0, _Metallic1, _Metallic2, _Metallic3;
    half _Smoothness0, _Smoothness1, _Smoothness2, _Smoothness3;
    half4 _DiffuseRemapScale0, _DiffuseRemapScale1, _DiffuseRemapScale2, _DiffuseRemapScale3;
    half4 _MaskMapRemapOffset0, _MaskMapRemapOffset1, _MaskMapRemapOffset2, _MaskMapRemapOffset3;
    half4 _MaskMapRemapScale0, _MaskMapRemapScale1, _MaskMapRemapScale2, _MaskMapRemapScale3;

    float4 _Control_ST;
    float4 _Control_TexelSize;
    half _DiffuseHasAlpha0, _DiffuseHasAlpha1, _DiffuseHasAlpha2, _DiffuseHasAlpha3;
    half _LayerHasMask0, _LayerHasMask1, _LayerHasMask2, _LayerHasMask3;
    half _SmoothnessSource0, _SmoothnessSource1, _SmoothnessSource2, _SmoothnessSource3;
    half4 _Splat0_ST, _Splat1_ST, _Splat2_ST, _Splat3_ST;
    // One set per GLOBAL layer index, not per pass-local one. Unity gives
    // the base pass layers 0-3 and the Add Pass layers 4-7, but BOTH passes
    // see the same material -- so if they shared a single set of four, the
    // Add Pass's layers would silently inherit layers 0-3's tint/parallax
    // settings. The LAYER_* aliases below pick the right four per pass.
    half4 _TintColor0, _TintColor1, _TintColor2, _TintColor3;
    half4 _TintColor4, _TintColor5, _TintColor6, _TintColor7;
    half4 _TintColor0B, _TintColor1B, _TintColor2B, _TintColor3B;
    half4 _TintColor4B, _TintColor5B, _TintColor6B, _TintColor7B;
    half _VariationScale0, _VariationScale1, _VariationScale2, _VariationScale3;
    half _VariationScale4, _VariationScale5, _VariationScale6, _VariationScale7;
    half _ParallaxScale0, _ParallaxScale1, _ParallaxScale2, _ParallaxScale3;
    half _ParallaxScale4, _ParallaxScale5, _ParallaxScale6, _ParallaxScale7;
    half _HeightTransition;
    half _NumLayersCount;
    float _TerrainBasemapDistance;

#ifdef UNITY_INSTANCING_ENABLED
    float4 _TerrainHeightmapRecipSize;   // float4(1.0f/width, 1.0f/height, 1.0f/(width-1), 1.0f/(height-1))
#endif
    float4 _TerrainHeightmapScale;       // float4(hmScale.x, hmScale.y / (float)(kMaxHeight), hmScale.z, 0.0f)
    #ifdef SCENESELECTIONPASS
    int _ObjectId;
    int _PassValue;
    #endif
CBUFFER_END

TEXTURE2D(_Control);    SAMPLER(sampler_Control);
TEXTURE2D(_Splat0);     SAMPLER(sampler_Splat0);
TEXTURE2D(_Splat1);     SAMPLER(sampler_Splat1);
TEXTURE2D(_Splat2);     SAMPLER(sampler_Splat2);
TEXTURE2D(_Splat3);     SAMPLER(sampler_Splat3);

TEXTURE2D(_Normal0);     SAMPLER(sampler_Normal0);
TEXTURE2D(_Normal1);     SAMPLER(sampler_Normal1);
TEXTURE2D(_Normal2);     SAMPLER(sampler_Normal2);
TEXTURE2D(_Normal3);     SAMPLER(sampler_Normal3);

TEXTURE2D(_Mask0);      SAMPLER(sampler_Mask0);
TEXTURE2D(_Mask1);      SAMPLER(sampler_Mask1);
TEXTURE2D(_Mask2);      SAMPLER(sampler_Mask2);
TEXTURE2D(_Mask3);      SAMPLER(sampler_Mask3);

// ------------------------------------------------------------------------
// STOCHASTIC TILING (added on top of Unity's stock terrain input code)
//
// Inigo Quilez's "texture repetition" technique: blend two copies of the
// same texture sampled at two randomly-offset UVs, the offsets picked from
// a hash of a slowly-varying procedural noise value, with a contrast-
// sharpened blend weight so the two copies never read as an obvious cross-
// fade. No pre-baked noise texture needed -- the noise is computed inline.
// https://iquilezles.org/articles/texturerepetition/
//
// Declared up here, ahead of the global height blend below, because that
// samples layer heights through it too -- a height has to land at the same
// offset as the albedo it belongs to.
// ------------------------------------------------------------------------

float TerrainStochasticHash1(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float TerrainStochasticValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float a = TerrainStochasticHash1(i);
    float b = TerrainStochasticHash1(i + float2(1, 0));
    float c = TerrainStochasticHash1(i + float2(0, 1));
    float d = TerrainStochasticHash1(i + float2(1, 1));
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}

// How large the "randomly offset" regions are, in the layer's own tiled UV
// space. Tuned once here rather than exposed as a material property so this
// shader's Properties block -- and therefore the stock TerrainLitShaderGUI
// inspector -- stays identical to Unity's own Terrain/Lit shader.
#define TERRAIN_STOCHASTIC_CELL_SCALE 0.5

half4 SampleTerrainLayerStochastic(TEXTURE2D_PARAM(tex, samp), float2 uv)
{
    float2 duvdx = ddx(uv);
    float2 duvdy = ddy(uv);

    float k = TerrainStochasticValueNoise(uv * TERRAIN_STOCHASTIC_CELL_SCALE);
    float l = k * 8.0;
    float f = frac(l);
    float ia = floor(l + 0.5);
    float ib = floor(l);
    f = min(f, 1.0 - f) * 2.0;

    float2 offA = sin(float2(3.0, 7.0) * ia);
    float2 offB = sin(float2(3.0, 7.0) * ib);

    half4 colA = SAMPLE_TEXTURE2D_GRAD(tex, samp, uv + offA, duvdx, duvdy);
    half4 colB = SAMPLE_TEXTURE2D_GRAD(tex, samp, uv + offB, duvdx, duvdy);

    half t = smoothstep(0.2, 0.8, f - 0.1 * ((colA.r + colA.g + colA.b) - (colB.r + colB.g + colB.b)));
    return lerp(colA, colB, t);
}

// ------------------------------------------------------------------------
// GLOBAL HEIGHT BLEND (>4 terrain layers)
//
// Every terrain layer's control map and Mask Map, bound as globals by
// TerrainGlobalHeightBlend.cs, so ANY pass can evaluate the height blend
// across ALL layers instead of only the 4 it was handed. See the comment
// on HeightBasedSplatModify in TerrainLitPassesStochastic.hlsl for why
// that's required above 4 layers.
//
// These are set with Shader.SetGlobal*, so they must NOT live in the
// UnityPerMaterial CBUFFER (that's material-scoped, and mixing the two
// breaks SRP Batcher compatibility).
//
// Samplers are the shared global ones from GlobalSamplers.hlsl rather than
// per-texture ones -- D3D11 allows only 16 active samplers and this file
// already declares a fair number, so 10 more would risk the limit for no
// benefit (mask maps want plain repeat, control maps want clamp).
#define GLOBAL_HEIGHT_MAX_LAYERS 8

TEXTURE2D(_GlobalControl0);
TEXTURE2D(_GlobalControl1);
TEXTURE2D(_GlobalMask0);
TEXTURE2D(_GlobalMask1);
TEXTURE2D(_GlobalMask2);
TEXTURE2D(_GlobalMask3);
TEXTURE2D(_GlobalMask4);
TEXTURE2D(_GlobalMask5);
TEXTURE2D(_GlobalMask6);
TEXTURE2D(_GlobalMask7);

// xy = tiling scale, zw = tiling offset (same convention as _SplatX_ST).
float4 _GlobalSplatST[GLOBAL_HEIGHT_MAX_LAYERS];
// x = height remap scale, y = height remap offset, z = 1 if this layer has
// a Mask Map at all (0 falls back to the flat 0.5 the shader's own
// SampleLayerMasks macro uses).
float4 _GlobalHeightRemap[GLOBAL_HEIGHT_MAX_LAYERS];
float4 _GlobalControlTexelSize;
float _GlobalLayerCount;

// One layer's height, sampled at that layer's own tiling -- at full
// per-pixel resolution, which is the whole point: the height data varies at
// texture-tiling frequency (a Tile Size of a few units across a
// thousand-unit terrain means hundreds of repetitions), so it can only be
// evaluated correctly here in the fragment shader.
// Routed through the same stochastic sampler the albedo and masks[] use, so
// a layer's height lands at the same tiling-breakup offset its visible
// texture does -- see the comment at the HeightBasedSplatModify call site.
#define SAMPLE_GLOBAL_LAYER_HEIGHT(idx, tex, meshUV, outHeight)                                     \
    {                                                                                               \
        float2 tiledUV = (meshUV) * _GlobalSplatST[idx].xy + _GlobalSplatST[idx].zw;                \
        half rawHeight = lerp(0.5h,                                                                 \
            SampleTerrainLayerStochastic(TEXTURE2D_ARGS(tex, sampler_LinearRepeat), tiledUV).b,     \
            _GlobalHeightRemap[idx].z);                                                             \
        outHeight = rawHeight * _GlobalHeightRemap[idx].x + _GlobalHeightRemap[idx].y;              \
    }

// Which global layer slot this pass's local layer 0 corresponds to. Unity
// hands layers 0-3 to the base pass and 4-7 to the Add Pass, so this maps a
// pass-local index onto the global arrays above. (Only valid up to 8
// layers, which is exactly the cap GLOBAL_HEIGHT_MAX_LAYERS sets -- beyond
// that there would be a second Add Pass with no way to tell it apart from
// the first.)
#ifdef TERRAIN_SPLAT_ADDPASS
    #define GLOBAL_HEIGHT_PASS_OFFSET 4
#else
    #define GLOBAL_HEIGHT_PASS_OFFSET 0
#endif

// Maps this pass's four local layer slots onto the right global set of
// per-layer material settings (see the CBUFFER comment above). Everything
// downstream refers to these aliases instead of the numbered properties
// directly, so the same code serves both passes.
#ifdef TERRAIN_SPLAT_ADDPASS
    #define LAYER_TINT_0        _TintColor4
    #define LAYER_TINT_1        _TintColor5
    #define LAYER_TINT_2        _TintColor6
    #define LAYER_TINT_3        _TintColor7
    #define LAYER_TINT_B_0      _TintColor4B
    #define LAYER_TINT_B_1      _TintColor5B
    #define LAYER_TINT_B_2      _TintColor6B
    #define LAYER_TINT_B_3      _TintColor7B
    #define LAYER_VARIATION_0   _VariationScale4
    #define LAYER_VARIATION_1   _VariationScale5
    #define LAYER_VARIATION_2   _VariationScale6
    #define LAYER_VARIATION_3   _VariationScale7
    #define LAYER_PARALLAX_0    _ParallaxScale4
    #define LAYER_PARALLAX_1    _ParallaxScale5
    #define LAYER_PARALLAX_2    _ParallaxScale6
    #define LAYER_PARALLAX_3    _ParallaxScale7
#else
    #define LAYER_TINT_0        _TintColor0
    #define LAYER_TINT_1        _TintColor1
    #define LAYER_TINT_2        _TintColor2
    #define LAYER_TINT_3        _TintColor3
    #define LAYER_TINT_B_0      _TintColor0B
    #define LAYER_TINT_B_1      _TintColor1B
    #define LAYER_TINT_B_2      _TintColor2B
    #define LAYER_TINT_B_3      _TintColor3B
    #define LAYER_VARIATION_0   _VariationScale0
    #define LAYER_VARIATION_1   _VariationScale1
    #define LAYER_VARIATION_2   _VariationScale2
    #define LAYER_VARIATION_3   _VariationScale3
    #define LAYER_PARALLAX_0    _ParallaxScale0
    #define LAYER_PARALLAX_1    _ParallaxScale1
    #define LAYER_PARALLAX_2    _ParallaxScale2
    #define LAYER_PARALLAX_3    _ParallaxScale3
#endif

// The global max height and normalization sum across every layer, matching
// exactly what Unity's own 4-layer HeightBasedSplatModify computes -- just
// over all N layers rather than one pass's slice of them.
//
// passHeights comes IN from the caller, sampled through Unity's own
// per-pass _Mask0-3 textures, samplers and UVs, and overrides the four
// global slots this pass owns. Sampling those layers here instead would
// mean the blend for the pass's own layers no longer matches stock Unity
// bit for bit (different sampler state, and tiling reconstructed in C#
// rather than whatever Unity actually bound), which showed up as the blend
// losing fine per-texel height detail and breaking into coarse blobs. The
// global textures are only needed for layers this pass ISN'T drawing --
// there's no per-pass equivalent to fall back on for those.
void ComputeGlobalHeightBlend(float2 meshUV, half transition, half4 passHeights, out half maxHeight, out half sumHeight)
{
    float2 controlUV = (meshUV * (_GlobalControlTexelSize.zw - 1.0f) + 0.5f) * _GlobalControlTexelSize.xy;
    half4 control0 = SAMPLE_TEXTURE2D(_GlobalControl0, sampler_LinearClamp, controlUV);
    half4 control1 = SAMPLE_TEXTURE2D(_GlobalControl1, sampler_LinearClamp, controlUV);

    half weights[GLOBAL_HEIGHT_MAX_LAYERS];
    weights[0] = control0.r; weights[1] = control0.g; weights[2] = control0.b; weights[3] = control0.a;
    weights[4] = control1.r; weights[5] = control1.g; weights[6] = control1.b; weights[7] = control1.a;

    half heights[GLOBAL_HEIGHT_MAX_LAYERS];
    SAMPLE_GLOBAL_LAYER_HEIGHT(0, _GlobalMask0, meshUV, heights[0]);
    SAMPLE_GLOBAL_LAYER_HEIGHT(1, _GlobalMask1, meshUV, heights[1]);
    SAMPLE_GLOBAL_LAYER_HEIGHT(2, _GlobalMask2, meshUV, heights[2]);
    SAMPLE_GLOBAL_LAYER_HEIGHT(3, _GlobalMask3, meshUV, heights[3]);
    SAMPLE_GLOBAL_LAYER_HEIGHT(4, _GlobalMask4, meshUV, heights[4]);
    SAMPLE_GLOBAL_LAYER_HEIGHT(5, _GlobalMask5, meshUV, heights[5]);
    SAMPLE_GLOBAL_LAYER_HEIGHT(6, _GlobalMask6, meshUV, heights[6]);
    SAMPLE_GLOBAL_LAYER_HEIGHT(7, _GlobalMask7, meshUV, heights[7]);

    // This pass's own four layers: use the values the caller already
    // sampled Unity's way, not the ones above. (The four sampled-then-
    // overwritten reads have no side effects, so the shader compiler drops
    // them.)
    heights[GLOBAL_HEIGHT_PASS_OFFSET + 0] = passHeights.x;
    heights[GLOBAL_HEIGHT_PASS_OFFSET + 1] = passHeights.y;
    heights[GLOBAL_HEIGHT_PASS_OFFSET + 2] = passHeights.z;
    heights[GLOBAL_HEIGHT_PASS_OFFSET + 3] = passHeights.w;

    half splatHeights[GLOBAL_HEIGHT_MAX_LAYERS];
    maxHeight = 0.0h;

    UNITY_UNROLL
    for (int i = 0; i < GLOBAL_HEIGHT_MAX_LAYERS; i++)
    {
        // Layers past the terrain's actual count contribute nothing rather
        // than reading whatever stale global happens to still be bound.
        half active = i < (int)_GlobalLayerCount ? 1.0h : 0.0h;
        splatHeights[i] = heights[i] * weights[i] * active;
        maxHeight = max(maxHeight, splatHeights[i]);
    }

    sumHeight = 0.0h;

    UNITY_UNROLL
    for (int j = 0; j < GLOBAL_HEIGHT_MAX_LAYERS; j++)
    {
        half active = j < (int)_GlobalLayerCount ? 1.0h : 0.0h;
        half weighted = max(0.0h, splatHeights[j] + transition - maxHeight);
        sumHeight += (weighted + 1e-6h) * weights[j] * active;
    }
}

TEXTURE2D(_MainTex);       SAMPLER(sampler_MainTex);
TEXTURE2D(_SpecGlossMap);  SAMPLER(sampler_SpecGlossMap);
TEXTURE2D(_MetallicTex);   SAMPLER(sampler_MetallicTex);

#if defined(UNITY_INSTANCING_ENABLED) && defined(_TERRAIN_INSTANCED_PERPIXEL_NORMAL)
#define ENABLE_TERRAIN_PERPIXEL_NORMAL
#endif

#ifdef UNITY_INSTANCING_ENABLED
TEXTURE2D(_TerrainHeightmapTexture);
TEXTURE2D(_TerrainNormalmapTexture);
SAMPLER(sampler_TerrainNormalmapTexture);
#endif

UNITY_INSTANCING_BUFFER_START(Terrain)
UNITY_DEFINE_INSTANCED_PROP(float4, _TerrainPatchInstanceData)  // float4(xBase, yBase, skipScale, ~)
UNITY_INSTANCING_BUFFER_END(Terrain)

#ifdef _ALPHATEST_ON
TEXTURE2D(_TerrainHolesTexture);
SAMPLER(sampler_TerrainHolesTexture);

float SampleTerrainHolesTexture(float2 uv)
{
    return SAMPLE_TEXTURE2D(_TerrainHolesTexture, sampler_TerrainHolesTexture, uv).r;
}

void ClipHoles(float2 uv)
{
    float hole = SampleTerrainHolesTexture(uv);
    // Fixes bug where compression is enabled and 0 isn't actually 0 but low like 1/2047. (UUM-61913)
    float epsilon = 0.0005f;
    clip(hole < epsilon ? -1 : 1);
}
#endif

#define SampleLayerAlbedo(i) (SAMPLE_TEXTURE2D(_Splat##i, sampler_Splat0, splat##i##uv) * half4(_DiffuseRemapScale##i.rgb, 1.0h))

#ifdef _NORMALMAP
    #define SampleLayerNormal(i) UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal##i, sampler_Normal0, splat##i##uv), _NormalScale##i)
#else
    #define SampleLayerNormal(i) half3(0.0, 0.0, 1.0)
#endif

#ifdef _MASKMAP
    #define SampleLayerMasks(i) (_MaskMapRemapOffset##i + _MaskMapRemapScale##i * lerp(0.5h, SAMPLE_TEXTURE2D(_Mask##i, sampler_Mask0, splat##i##uv), _LayerHasMask##i));
#else
    #define SampleLayerMasks(i) (_MaskMapRemapOffset##i + _MaskMapRemapScale##i * 0.5h);
#endif

half4 SampleMetallicSpecGloss(float2 uv, half albedoAlpha)
{
    half4 specGloss;
    specGloss = SAMPLE_TEXTURE2D(_MetallicTex, sampler_MetallicTex, uv);
    specGloss.a = albedoAlpha;
    return specGloss;
}

inline void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData outSurfaceData)
{
    outSurfaceData = (SurfaceData)0;
    half4 albedoSmoothness = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
    outSurfaceData.alpha = 1;

    half4 specGloss = SampleMetallicSpecGloss(uv, albedoSmoothness.a);
    outSurfaceData.albedo = albedoSmoothness.rgb;

    outSurfaceData.metallic = specGloss.r;
    outSurfaceData.specular = half3(0.0h, 0.0h, 0.0h);

    outSurfaceData.smoothness = specGloss.a;
    outSurfaceData.normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap));
    outSurfaceData.occlusion = 1;
    outSurfaceData.emission = 0;
}

void TerrainInstancing(inout float4 positionOS, inout float3 normal, inout float2 uv)
{
#ifdef UNITY_INSTANCING_ENABLED
    float2 patchVertex = positionOS.xy;
    float4 instanceData = UNITY_ACCESS_INSTANCED_PROP(Terrain, _TerrainPatchInstanceData);

    float2 sampleCoords = (patchVertex.xy + instanceData.xy) * instanceData.z; // (xy + float2(xBase,yBase)) * skipScale
    float height = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(sampleCoords, 0)));

    positionOS.xz = sampleCoords * _TerrainHeightmapScale.xz;
    positionOS.y = height * _TerrainHeightmapScale.y;

#ifdef ENABLE_TERRAIN_PERPIXEL_NORMAL
    normal = float3(0, 1, 0);
#else
    normal = _TerrainNormalmapTexture.Load(int3(sampleCoords, 0)).rgb * 2 - 1;
#endif
    uv = sampleCoords * _TerrainHeightmapRecipSize.zw;
#endif
}

void TerrainInstancing(inout float4 positionOS, inout float3 normal)
{
    float2 uv = { 0, 0 };
    TerrainInstancing(positionOS, normal, uv);
}

void TerrainInstancing(inout float4 positionOS)
{
    float3 normal = { 0, 0, 0 };
    TerrainInstancing(positionOS, normal);
}

// ------------------------------------------------------------------------
// PER-LAYER PARALLAX
//
// Terrain has no per-layer heightmap of its own -- each layer's Mask Map
// already carries a height value in its Blue channel (used above for
// Height-Based Blend), so that's reused here as the parallax height source
// instead of adding a whole separate texture slot.
//
// A terrain patch has no meaningful per-pixel tangent basis in the common
// (GPU-instanced, per-pixel-normal) case, but it doesn't need one: a
// Terrain's UV already maps 1:1 onto world X/Z, so the surface's local
// tangent-space X/Y axes for parallax purposes just ARE world X/Z, and the
// "normal" axis is world Y. That lets Unity's own ParallaxOffset formula
// (Built-in's Parallax Mapping.cginc) be reused verbatim with world-space
// view direction standing in for tangent-space view direction.
// ------------------------------------------------------------------------

float2 TerrainParallaxOffset(half height, half heightScale, half3 viewDirWS)
{
    height = height * heightScale - heightScale * 0.5h;
    half3 v = normalize(viewDirWS);
    v.y += 0.42h; // matches Unity's own ParallaxOffset bias, avoids blowup at grazing angles
    return height * (v.xz / v.y);
}

#endif

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;

// Draws held items after the world and over it, at a field of view of their own.
//
// One camera, three passes. The alternative -- a second camera stacked on the first
// -- gets the same picture, at the cost of a second cull, a second transform to keep
// in step with the first, and a depth buffer the post stack cannot see into. This
// stays inside the one camera and just changes what is drawn when, and with which
// projection.
//
// The FOV override is the reason it is a projection matrix rather than a camera
// setting. A weapon sits centimetres from the lens, where a wide world FOV stretches
// whatever is nearest the frame edges -- so a weapon authored to look right comes out
// bent and enormous. Substituting a narrower projection for this one draw fixes the
// weapon's shape without touching how much of the world is visible, which is the only
// thing a player actually judges a FOV setting by.
//
// Brought over from an earlier project of the user's (YalcinsBoomer); kept close to
// the original, which was already doing the right things.
public class ViewmodelOverlayFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        [Header("Viewmodel Rendering")]
        [Tooltip("Viewmodel objelerinin bulunduğu layer'lar (örn: ViewModel + Shell)")]
        public LayerMask viewmodelLayers = 0;

        [Tooltip("Viewmodel için ayrı FOV kullan")]
        public bool overrideFOV = true;

        [Tooltip("Viewmodel FOV değeri")]
        public float viewmodelFOV = 60f;

        [Header("Viewmodel Ambient Occlusion")]
        // The weapon's own creases and cavities, from the same estimator URP's
        // SSAO uses (Alchemy / Morgan 2011) over the overlay's own depth.
        [Tooltip("Silahın kendi girinti ve köşelerindeki AO")]
        public bool enableAO = true;

        [Tooltip("AO sample yarıçapı (piksel)")]
        [Range(1f, 20f)]
        public float aoRadius = 5f;

        [Tooltip("AO karartma şiddeti")]
        [Range(0f, 5f)]
        public float aoIntensity = 1.5f;

        [Tooltip("AO derinlik eşiği — düz yüzeylerde self-occlusion'ı önler")]
        [Range(0.001f, 0.1f)]
        public float aoBias = 0.01f;

        [Header("Viewmodel Edge Occlusion")]
        // A soft darkening that sits across the weapon's silhouette, reaching both
        // inward onto the weapon and outward into the room. Not a line: a line is a
        // drawn border and reads as one, where this reads as the weapon being in front
        // of the room and taking light off it.
        //
        // Computed in the same sampling loop as the AO above and smoothed by the same
        // blur -- it is the same question at a wider radius, so asking it separately
        // only bought a second pass and a second way for the two to disagree.
        [Tooltip("Silahın siluetinin iki yanına taşan yumuşak karartma")]
        public bool enableOutline = false;

        [Tooltip("Karartmanın rengi. Alpha genel şiddeti ölçekler")]
        public Color outlineColor = Color.black;

        [Tooltip("Siluetin iki yanına ne kadar taştığı (piksel)")]
        [Range(2f, 64f)]
        public float outlineRadius = 24f;

        [Tooltip("Siluetin üstündeki en koyu değer")]
        [Range(0f, 2f)]
        public float outlineIntensity = 0.8f;

        [Tooltip("Yayılma eğrisi. 1'in üstü siluete yapıştırır, altı daha uzağa dağıtır")]
        [Range(0.25f, 4f)]
        public float outlineFalloff = 1.5f;
    }

    public Settings settings = new Settings();

    // Runtime FOV override — PlayerLook drives this while an item is aiming.
    // -1 = use settings.viewmodelFOV.
    public static float runtimeViewmodelFOV = -1f;

    // What the renderer asset was authored with, published so anything easing into an
    // override has something to ease back TO.
    //
    // Without it the base figure would have to be written down a second time on the
    // player, and two copies of one number is how the weapon ends up settling to a
    // different FOV than the one the artist set.
    public static float SettingsFOV { get; private set; } = 60f;

    /// <summary>
    /// Aktif viewmodel FOV: runtime override varsa onu, yoksa settings değerini döner
    /// </summary>
    public static float ActiveViewmodelFOV(float settingsFOV)
    {
        return runtimeViewmodelFOV > 0f ? runtimeViewmodelFOV : settingsFOV;
    }

    private ViewmodelColorPass _colorPass;
    private ViewmodelDepthMergePass _depthMergePass;
    private ViewmodelAOPass _aoPass;
    private Material _aoMaterial;

    // A material of its own for each blur direction.
    //
    // A render graph pass body does not draw when it runs -- it appends to a command
    // buffer that is submitted once everything has been recorded -- and a command
    // holds a reference to its material, not a copy of its values. So one material
    // written twice in a frame does not give two draws two settings; it gives both
    // draws whichever value was written last. The horizontal and vertical steps are
    // the only two settings in this shader that collide that way, and sharing them
    // meant both passes blurred vertically and the horizontal noise was never
    // touched.
    private Material _aoBlurHorizontalMaterial;
    private Material _aoBlurVerticalMaterial;

    private Material _depthMergeMaterial;

    private static readonly string AO_SHADER_NAME = "Hidden/PostProcessing/ViewmodelAO";
    private static readonly string DEPTH_MERGE_SHADER_NAME = "Hidden/PostProcessing/ViewmodelDepthMerge";

    public override void Create()
    {
        // Re-published on every rebuild -- a domain reload, an Inspector edit -- so it
        // tracks the asset instead of being a snapshot of it.
        SettingsFOV = settings.viewmodelFOV;

        _colorPass = new ViewmodelColorPass(settings);

        var depthMergeShader = Shader.Find(DEPTH_MERGE_SHADER_NAME);
        if (depthMergeShader != null)
        {
            _depthMergeMaterial = CoreUtils.CreateEngineMaterial(depthMergeShader);
            _depthMergePass = new ViewmodelDepthMergePass(_depthMergeMaterial);
        }

        var shader = Shader.Find(AO_SHADER_NAME);
        if (shader != null)
        {
            _aoMaterial = CoreUtils.CreateEngineMaterial(shader);
            _aoBlurHorizontalMaterial = CoreUtils.CreateEngineMaterial(shader);
            _aoBlurVerticalMaterial = CoreUtils.CreateEngineMaterial(shader);
            _aoPass = new ViewmodelAOPass(
                settings, _aoMaterial, _aoBlurHorizontalMaterial, _aoBlurVerticalMaterial);
        }
    }

    protected override void Dispose(bool disposing)
    {
        _colorPass?.Cleanup();
        CoreUtils.Destroy(_aoMaterial);
        _aoMaterial = null;
        CoreUtils.Destroy(_aoBlurHorizontalMaterial);
        _aoBlurHorizontalMaterial = null;
        CoreUtils.Destroy(_aoBlurVerticalMaterial);
        _aoBlurVerticalMaterial = null;
        CoreUtils.Destroy(_depthMergeMaterial);
        _depthMergeMaterial = null;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        CameraType cameraType = renderingData.cameraData.cameraType;

        // The scene view gets the colour pass and nothing else.
        //
        // Without it the layer is invisible there, because the renderer's opaque mask
        // excludes it and this feature was the only thing drawing it -- so a weapon
        // could not be placed, its grips could not be lined up, and its muzzle could
        // not be aimed, which is most of the work a weapon needs.
        //
        // The other two passes are deliberately left out. They exist to make the
        // overlay agree with a game frame -- to give the item the depth of field and
        // the ambient occlusion the world is getting -- and a scene view has neither
        // to agree with.
        if (cameraType != CameraType.Game && cameraType != CameraType.SceneView)
            return;

        if (_colorPass != null)
            renderer.EnqueuePass(_colorPass);

        if (cameraType != CameraType.Game)
            return;

        if (_depthMergePass != null && _depthMergeMaterial != null)
            renderer.EnqueuePass(_depthMergePass);

        // One pass for both. Either switch on its own is enough to want it; the one
        // that is off contributes nothing because its strength goes in as zero.
        if ((settings.enableAO || settings.enableOutline) && _aoPass != null && _aoMaterial != null)
            renderer.EnqueuePass(_aoPass);
    }

    // ─────────────────────────────────────────────────────────────
    // Pass 1: Color — silahları sahnenin üstüne çizer
    //         Depth temizler, viewmodel FOV ile çizer
    // ─────────────────────────────────────────────────────────────
    class ViewmodelColorPass : ScriptableRenderPass
    {
        private Settings _settings;

        private static readonly List<ShaderTagId> s_shaderTags = new List<ShaderTagId>
        {
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly"),
            new ShaderTagId("SRPDefaultUnlit")
        };

        // Built by name rather than taken from URP's own ShaderGlobalKeywords, which
        // is internal to the package. The name is the same one the project's custom
        // lit shaders declare in their multi_compile.
        private static readonly GlobalKeyword s_ScreenSpaceOcclusion =
            GlobalKeyword.Create("_SCREEN_SPACE_OCCLUSION");

        private class PassData
        {
            public RendererListHandle rendererList;
            public Matrix4x4 viewMatrix;
            public Matrix4x4 viewmodelProjection;
            public Matrix4x4 originalProjection;
            public bool overrideFOV;
            public bool occlusionWasEnabled;

            // A game frame wants the overlay treatment: depth thrown away so the item
            // sits over the world, and a projection of its own. A scene view wants the
            // opposite of both -- the item where it actually is, behind whatever is
            // actually in front of it, through the lens the person is navigating with.
            // One is a first-person view, the other is a workspace.
            public bool asOverlay;
        }

        public ViewmodelColorPass(Settings settings)
        {
            _settings = settings;

            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        }

        public void Cleanup() { }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var urData = frameData.Get<UniversalRenderingData>();
            var camData = frameData.Get<UniversalCameraData>();
            var ltData = frameData.Get<UniversalLightData>();

            Camera cam = camData.camera;

            // --- Viewmodel Draw ---
            var sortingCriteria = SortingCriteria.CommonOpaque;
            var drawingSettings = RenderingUtils.CreateDrawingSettings(
                s_shaderTags, urData, camData, ltData, sortingCriteria);

            var filterSettings = new FilteringSettings(RenderQueueRange.all, _settings.viewmodelLayers);
            var rlParams = new RendererListParams(urData.cullResults, drawingSettings, filterSettings);
            var rendererList = renderGraph.CreateRendererList(rlParams);

            bool asOverlay = camData.cameraType == CameraType.Game;

            Matrix4x4 viewMatrix = cam.worldToCameraMatrix;
            Matrix4x4 originalProj = cam.projectionMatrix;
            Matrix4x4 viewmodelProj = originalProj;

            if (asOverlay && _settings.overrideFOV)
            {
                float fov = ViewmodelOverlayFeature.ActiveViewmodelFOV(_settings.viewmodelFOV);
                viewmodelProj = Matrix4x4.Perspective(
                    fov, cam.aspect, cam.nearClipPlane, cam.farClipPlane);
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Viewmodel Overlay", out var passData))
            {
                passData.rendererList = rendererList;
                passData.viewMatrix = viewMatrix;
                passData.viewmodelProjection = viewmodelProj;
                passData.originalProjection = originalProj;
                passData.overrideFOV = asOverlay && _settings.overrideFOV;
                passData.asOverlay = asOverlay;
                passData.occlusionWasEnabled = Shader.IsKeywordEnabled(s_ScreenSpaceOcclusion);

                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);
                builder.UseRendererList(rendererList);

                // Declared because the occlusion keyword below is a global, and render
                // graph refuses global changes from a pass that has not said it makes
                // them -- it has to know, or it cannot reason about what the passes
                // around this one are compiled against.
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    // Depth buffer'ı temizle — silahlar her şeyin üstünde çizilsin.
                    // Scene view'da temizlenmez: orada eşya dünyanın içinde durur ve
                    // önündeki şeyin arkasında kalması doğru olandır.
                    if (data.asOverlay)
                        ctx.cmd.ClearRenderTarget(true, false, Color.clear);

                    // The world's ambient occlusion, off for this draw.
                    //
                    // With the renderer's SSAO set to "After Opaque: off", URP does not
                    // composite AO into the image -- it hands every lit shader an
                    // occlusion texture and has each pixel darken itself by whatever it
                    // finds at its OWN screen position. That texture was built from the
                    // world's depth and normals, at the world's projection, and knows
                    // nothing about a weapon. So the weapon reads the occlusion of
                    // whatever happens to be behind it and wears the room's AO as a
                    // pattern painted across the receiver.
                    //
                    // Turning the keyword off for these draws is what stops that. The
                    // weapon is not left without AO: the pass further down gives it its
                    // own, computed from depth that actually contains a weapon.
                    //
                    // Only in the game view. In the scene view the item IS part of the
                    // world, drawn at the world's projection, so the world's occlusion
                    // is the right occlusion -- and that view gets no AO pass of its
                    // own to replace it with.
                    if (data.asOverlay)
                        ctx.cmd.SetKeyword(s_ScreenSpaceOcclusion, false);

                    if (data.overrideFOV)
                        ctx.cmd.SetViewProjectionMatrices(data.viewMatrix, data.viewmodelProjection);

                    ctx.cmd.DrawRendererList(data.rendererList);

                    if (data.overrideFOV)
                        ctx.cmd.SetViewProjectionMatrices(data.viewMatrix, data.originalProjection);

                    // Restored rather than left off, because the keyword is global and
                    // the next camera -- or the next frame's world -- still wants it.
                    if (data.asOverlay)
                        ctx.cmd.SetKeyword(s_ScreenSpaceOcclusion, data.occlusionWasEnabled);
                });
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Pass 2: Depth Merge — silah depth'ini _CameraDepthTexture'a yazar
    //         DoF gibi post-process efektleri doğru mesafeyi görsün
    // ─────────────────────────────────────────────────────────────
    class ViewmodelDepthMergePass : ScriptableRenderPass
    {
        private Material _material;

        private static readonly int WeaponDepthTexId = Shader.PropertyToID("_WeaponDepthTex");
        private static readonly int CameraDepthTexId = Shader.PropertyToID("_CameraDepthTexture");

        private class PassData
        {
            public Material material;
            public TextureHandle cameraDepth;
            public TextureHandle weaponDepth;
        }

        public ViewmodelDepthMergePass(Material material)
        {
            _material = material;

            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraDepth = resourceData.cameraDepthTexture;
            var activeDepth = resourceData.activeDepthTexture;

            if (!cameraDepth.IsValid() || !activeDepth.IsValid())
                return;

            // Single-channel float, point-sampled: this is a depth value per pixel, and
            // anything that interpolates between two of them invents a surface halfway
            // between the weapon and the wall behind it.
            var desc = renderGraph.GetTextureDesc(cameraDepth);
            desc.name = "ViewmodelMergedDepth";
            desc.format = GraphicsFormat.R32_SFloat;
            desc.depthBufferBits = 0;
            desc.filterMode = FilterMode.Point;
            desc.clearBuffer = false;

            var merged = renderGraph.CreateTexture(desc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Viewmodel Depth Merge", out var passData))
            {
                passData.material = _material;
                passData.cameraDepth = cameraDepth;
                passData.weaponDepth = activeDepth;

                builder.UseTexture(cameraDepth, AccessFlags.Read);
                builder.UseTexture(activeDepth, AccessFlags.Read);
                builder.SetRenderAttachment(merged, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetGlobalTextureAfterPass(merged, CameraDepthTexId);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalTexture(WeaponDepthTexId, data.weaponDepth);
                    Blitter.BlitTexture(ctx.cmd, data.cameraDepth, new Vector4(1, 1, 0, 0), data.material, 0);
                });
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Pass 3: AO + Edge Occlusion — tek SSAO hesabı
    //         Silahın kendi AO'su ve siluetten taşan karartma
    //         aynı örnekleme döngüsünden çıkar, aynı blur'dan geçer
    // ─────────────────────────────────────────────────────────────
    class ViewmodelAOPass : ScriptableRenderPass
    {
        private Settings _settings;
        private Material _material;
        private Material _blurHorizontalMaterial;
        private Material _blurVerticalMaterial;

        private const int ComputePass = 0;
        private const int BlurPass = 1;
        private const int CompositePass = 2;

        private static readonly int AORadiusId = Shader.PropertyToID("_AORadius");
        private static readonly int AOIntensityId = Shader.PropertyToID("_AOIntensity");
        private static readonly int AOBiasId = Shader.PropertyToID("_AOBias");
        private static readonly int SpillColorId = Shader.PropertyToID("_SpillColor");
        private static readonly int SpillRadiusId = Shader.PropertyToID("_SpillRadius");
        private static readonly int SpillIntensityId = Shader.PropertyToID("_SpillIntensity");
        private static readonly int SpillFalloffId = Shader.PropertyToID("_SpillFalloff");
        private static readonly int ScreenSizeId = Shader.PropertyToID("_ViewmodelScreenSize");
        private static readonly int TanHalfId = Shader.PropertyToID("_ViewmodelTanHalf");
        private static readonly int BlurStepId = Shader.PropertyToID("_BlurStep");
        private static readonly int AOTexId = Shader.PropertyToID("_ViewmodelAOTex");
        private static readonly int ViewmodelDepthTexId = Shader.PropertyToID("_ViewmodelDepthTex");

        private class PassData
        {
            public Material material;
            public TextureHandle source;
            public TextureHandle depthTexture;
            public TextureHandle aoTexture;
            public int shaderPass;

            public Vector4 screenSize;
            public Vector2 tanHalf;
            public float aoRadius;
            public float aoIntensity;
            public float aoBias;
            public Color spillColor;
            public float spillRadius;
            public float spillIntensity;
            public float spillFalloff;
            public Vector2 blurStep;
        }

        public ViewmodelAOPass(Settings settings, Material material,
                               Material blurHorizontalMaterial, Material blurVerticalMaterial)
        {
            _settings = settings;
            _material = material;
            _blurHorizontalMaterial = blurHorizontalMaterial;
            _blurVerticalMaterial = blurVerticalMaterial;

            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var camData = frameData.Get<UniversalCameraData>();

            var source = resourceData.activeColorTexture;

            // The overlay pass cleared this and drew only the held items into it, so it
            // is a weapon-shaped stencil as much as it is a depth buffer -- which is
            // what makes a silhouette findable at all. Against the scene's own depth the
            // weapon has no edge; it just continues into whatever is behind it.
            var depth = resourceData.activeDepthTexture;

            if (!source.IsValid() || !depth.IsValid())
                return;

            var sourceDesc = renderGraph.GetTextureDesc(source);

            int fullWidth = Mathf.Max(1, sourceDesc.width);
            int fullHeight = Mathf.Max(1, sourceDesc.height);

            // Half resolution. Nothing is lost -- the result is blurred, so it has no
            // detail finer than the blur to lose -- and it buys a quarter of the fill
            // plus a free extra smoothing step, because the last read back up to full
            // size is bilinear.
            int aoWidth = Mathf.Max(1, fullWidth / 2);
            int aoHeight = Mathf.Max(1, fullHeight / 2);

            var aoDesc = sourceDesc;
            aoDesc.width = aoWidth;
            aoDesc.height = aoHeight;

            // Two channels: occlusion and coverage. Sixteen-bit float rather than byte
            // because occlusion is a gradient, and 256 steps of one across a smooth
            // surface is visible as banding after the blur has removed the noise that
            // was hiding it.
            aoDesc.format = GraphicsFormat.R16G16_SFloat;
            aoDesc.depthBufferBits = 0;
            aoDesc.filterMode = FilterMode.Bilinear;
            aoDesc.clearBuffer = false;
            aoDesc.msaaSamples = MSAASamples.None;

            aoDesc.name = "ViewmodelAO_A";
            var aoA = renderGraph.CreateTexture(aoDesc);

            aoDesc.name = "ViewmodelAO_B";
            var aoB = renderGraph.CreateTexture(aoDesc);

            // The viewmodel is drawn through a projection of its own, so its depth has
            // to be unprojected through that one. Taken from the same helper the colour
            // pass builds its matrix from, or the reconstruction would disagree with the
            // picture by exactly the FOV override.
            Camera cam = camData.camera;
            float fov = _settings.overrideFOV
                ? ViewmodelOverlayFeature.ActiveViewmodelFOV(_settings.viewmodelFOV)
                : cam.fieldOfView;

            float tanHalfY = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            var tanHalf = new Vector2(tanHalfY * cam.aspect, tanHalfY);

            // A switch that is off goes in as zero strength rather than as a branch, so
            // the two effects can be turned on and off independently while still sharing
            // the one pass.
            float aoIntensity = _settings.enableAO ? _settings.aoIntensity : 0f;
            float spillIntensity = _settings.enableOutline ? _settings.outlineIntensity : 0f;

            var screenSize = new Vector4(fullWidth, fullHeight, 1f / fullWidth, 1f / fullHeight);

            // 1: occlusion + coverage, at half size
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Viewmodel AO", out var passData))
            {
                passData.material = _material;
                passData.depthTexture = depth;
                passData.shaderPass = ComputePass;
                passData.screenSize = screenSize;
                passData.tanHalf = tanHalf;
                passData.aoRadius = _settings.aoRadius;
                passData.aoIntensity = aoIntensity;
                passData.aoBias = _settings.aoBias;
                passData.spillRadius = _settings.outlineRadius;

                builder.UseTexture(depth, AccessFlags.Read);
                builder.SetRenderAttachment(aoA, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.material.SetVector(ScreenSizeId, data.screenSize);
                    data.material.SetVector(TanHalfId, data.tanHalf);
                    data.material.SetFloat(AORadiusId, data.aoRadius);
                    data.material.SetFloat(AOIntensityId, data.aoIntensity);
                    data.material.SetFloat(AOBiasId, data.aoBias);
                    data.material.SetFloat(SpillRadiusId, data.spillRadius);
                    ctx.cmd.SetGlobalTexture(ViewmodelDepthTexId, data.depthTexture);
                    Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                });
            }

            // 2 and 3: across, then down. Separable, so the cost is two rows of taps
            // rather than a square of them -- and this is the step that turns twelve
            // samples per pixel into something without a pattern in it.
            BlurStep(renderGraph, _blurHorizontalMaterial, aoA, aoB, new Vector2(1f / aoWidth, 0f));
            BlurStep(renderGraph, _blurVerticalMaterial, aoB, aoA, new Vector2(0f, 1f / aoHeight));

            // 4: over the colour
            var tempDesc = sourceDesc;
            tempDesc.name = "ViewmodelAO_Temp";
            var tempTexture = renderGraph.CreateTexture(tempDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Viewmodel AO Composite", out var passData))
            {
                passData.material = _material;
                passData.source = source;
                passData.aoTexture = aoA;
                passData.shaderPass = CompositePass;
                passData.spillColor = _settings.outlineColor;
                passData.spillIntensity = spillIntensity;
                passData.spillFalloff = _settings.outlineFalloff;

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(aoA, AccessFlags.Read);
                builder.SetRenderAttachment(tempTexture, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.material.SetColor(SpillColorId, data.spillColor);
                    data.material.SetFloat(SpillIntensityId, data.spillIntensity);
                    data.material.SetFloat(SpillFalloffId, data.spillFalloff);
                    ctx.cmd.SetGlobalTexture(AOTexId, data.aoTexture);
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                });
            }

            // 5: back onto the camera colour
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Viewmodel AO Copy Back", out var passData))
            {
                passData.source = tempTexture;

                builder.UseTexture(tempTexture, AccessFlags.Read);
                builder.SetRenderAttachment(source, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), 0, false);
                });
            }
        }

        private void BlurStep(RenderGraph renderGraph, Material material,
                              TextureHandle from, TextureHandle to, Vector2 step)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Viewmodel AO Blur", out var passData))
            {
                passData.material = material;
                passData.aoTexture = from;
                passData.blurStep = step;
                passData.shaderPass = BlurPass;

                builder.UseTexture(from, AccessFlags.Read);
                builder.SetRenderAttachment(to, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.material.SetVector(BlurStepId, data.blurStep);
                    ctx.cmd.SetGlobalTexture(AOTexId, data.aoTexture);
                    Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                });
            }
        }
    }
}

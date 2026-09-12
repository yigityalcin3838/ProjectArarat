using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

// Depth of field that knows where the held item actually is, with autofocus measured off
// the frame rather than off the physics world.
//
// EVERY SETTING LIVES ON THE VOLUME, not here. See NearFieldDepthOfFieldVolume: the
// project already has an indoor profile and an outdoor one, a corridor wants a different
// focal range from a mountainside, and gameplay has to be able to move focus during a
// reload. All three are what volumes are for. This feature holds the shader and one
// structural switch, and nothing an artist or a designer would reach for.
//
// ── WHY THIS EXISTS AT ALL ───────────────────────────────────────────────────────────
//
// URP's own was switched off because the held item is drawn through a lens of its own, so
// its pixels are nowhere near where _CameraDepthTexture says they are. Anything that
// reads depth and then samples neighbouring pixels smears the background's blur across
// the weapon's silhouette or punches a hole in it. That is two projections arguing over
// one depth buffer, and no parameter on URP's effect fixes it -- the fix has to be a
// depth buffer both projections agree on, which is the first pass here.
//
// ── ORDERING, AND IT IS A REAL REQUIREMENT ───────────────────────────────────────────
//
// THIS FEATURE MUST SIT BELOW ViewModelLensFeature IN THE RENDERER'S FEATURE LIST.
//
// It reads the depth buffer that feature leaves behind: the lens pass clears depth and
// draws only the held item, so from that point the camera's depth attachment is a
// foreground-only buffer. Passes at the same injection point run in the order their
// features were enqueued, which is list order, so above it this feature would merge a
// depth buffer that still holds the world and the effect would quietly be ordinary.
//
// Both sit at BeforeRenderingPostProcessing rather than later, so the blur happens before
// tonemapping and bloom -- which is what gives bokeh its punch, since it is the bright
// values before the curve that spread.
//
// ── WHICH LAYERS ARE "IN FRONT" ──────────────────────────────────────────────────────
//
// Not authored here either. The foreground is whatever the lens feature drew, which is
// its View Model Layers -- so that field is the layer selection, and it is the same field
// that decides what gets the lens in the first place. Stating it twice is how the depth
// of field ends up disagreeing with the picture about which pixels are the weapon.
public class NearFieldDepthOfFieldFeature : ScriptableRendererFeature
{
    [Tooltip("Hidden/PostProcessing/NearFieldDepthOfField. Assign NearFieldDepthOfField.shader.")]
    [SerializeField] private Shader depthOfFieldShader;

    // Structural rather than artistic, which is why it is here and not on the volume: off
    // means this effect stops treating the held item as a separate thing at all. Useful
    // for seeing what the merge is buying, and correct for a camera with nothing in hand.
    [Tooltip("Composite the held item's own depth over the world's before measuring.")]
    [SerializeField] private bool foregroundOnTop = true;

    private NearFieldDepthOfFieldPass _pass;
    private Material _material;

    // The focus distance has to survive to next frame, so it cannot be a render graph
    // texture -- those live and die inside one frame. Two of them, ping-ponged, because a
    // pass cannot read and write the same attachment and the smoothing needs both.
    private RTHandle _focusA;
    private RTHandle _focusB;
    private bool _focusParity;

    public override void Create()
    {
        _pass = new NearFieldDepthOfFieldPass();

        CoreUtils.Destroy(_material);
        _material = depthOfFieldShader != null
            ? CoreUtils.CreateEngineMaterial(depthOfFieldShader)
            : null;
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
        _material = null;

        _focusA?.Release();
        _focusB?.Release();

        _focusA = null;
        _focusB = null;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null || _material == null)
            return;

        if (renderingData.cameraData.cameraType != CameraType.Game)
            return;

        var volume = VolumeManager.instance.stack.GetComponent<NearFieldDepthOfFieldVolume>();

        // No component in any profile, or its radius is zero. Off has to mean "enqueue
        // nothing" rather than "blur by nothing": five passes that reproduce the original
        // image are five passes wasted.
        if (volume == null || !volume.IsActive())
            return;

        EnsureFocusTargets();

        // Swapped every frame, so what one frame wrote the next frame reads. The pass is
        // told which is which rather than working it out, because "the one that is not
        // the other one" is the sort of thing that inverts during a refactor and then
        // silently stops smoothing.
        _focusParity = !_focusParity;

        RTHandle current = _focusParity ? _focusA : _focusB;
        RTHandle previous = _focusParity ? _focusB : _focusA;

        var mode = volume.focusMode.value;

        // Converted from a rate to a per-frame fraction here, where the delta time is,
        // and framerate-independent: a fixed fraction per frame would ease at one speed at
        // 60 fps and another at 144. The pinned modes land outright -- easing towards a
        // constant that is already known is latency for nothing.
        float lerp = mode == NearFieldDepthOfFieldVolume.FocusMode.Autofocus
            ? 1.0f - Mathf.Exp(-volume.focusSpeed.value * Mathf.Max(Time.deltaTime, 1e-5f))
            : 1.0f;

        float pinned = mode switch
        {
            NearFieldDepthOfFieldVolume.FocusMode.Foreground => volume.foregroundDistance.value,
            NearFieldDepthOfFieldVolume.FocusMode.Manual => volume.manualDistance.value,
            _ => 0.0f,
        };

        var settings = new NearFieldDepthOfFieldPass.Settings
        {
            material = _material,
            foregroundOnTop = foregroundOnTop,
            autofocus = mode == NearFieldDepthOfFieldVolume.FocusMode.Autofocus,
            pinnedDistance = pinned,
            focusPoint = volume.focusPoint.value,
            focusSearchRadius = volume.focusSearchRadius.value,
            focusLerp = lerp,
            autofocusMaxDistance = volume.autofocusMaxDistance.value,
            focusRange = volume.focusRange.value,
            blurFalloff = volume.blurFalloff.value,
            maxBlurRadius = volume.maxBlurRadius.value,
            foregroundBlurScale = volume.foregroundBlurScale.value,
            foregroundBlurFloor = volume.foregroundBlurFloor.value,
            focusCurrent = current,
            focusPrevious = previous,
        };

        _pass.Setup(settings);
        renderer.EnqueuePass(_pass);
    }

    private void EnsureFocusTargets()
    {
        if (_focusA != null && _focusB != null)
            return;

        // One pixel, two channels, full float. Red is the focal distance in metres, green
        // is how engaged the effect is -- and the distance is why this is 32-bit rather
        // than 16: sixteen bits would quantise the far end of a large scene into visible
        // focus steps.
        _focusA = RTHandles.Alloc(1, 1, colorFormat: GraphicsFormat.R32G32_SFloat,
                                  filterMode: FilterMode.Point, name: "NearFieldFocusA");
        _focusB = RTHandles.Alloc(1, 1, colorFormat: GraphicsFormat.R32G32_SFloat,
                                  filterMode: FilterMode.Point, name: "NearFieldFocusB");
    }

    private class NearFieldDepthOfFieldPass : ScriptableRenderPass
    {
        public struct Settings
        {
            public Material material;
            public bool foregroundOnTop;
            public bool autofocus;
            public float pinnedDistance;
            public Vector2 focusPoint;
            public float focusSearchRadius;
            public float focusLerp;
            public float autofocusMaxDistance;
            public float focusRange;
            public float blurFalloff;
            public float maxBlurRadius;
            public float foregroundBlurScale;
            public float foregroundBlurFloor;
            public RTHandle focusCurrent;
            public RTHandle focusPrevious;
        }

        private const int MergeShaderPass = 0;
        private const int FocusShaderPass = 1;
        private const int PrepareShaderPass = 2;
        private const int GatherShaderPass = 3;
        private const int CompositeShaderPass = 4;

        private static readonly int ScreenSizeId = Shader.PropertyToID("_DofScreenSize");
        private static readonly int FocusPointId = Shader.PropertyToID("_FocusPoint");
        private static readonly int FocusSearchRadiusId = Shader.PropertyToID("_FocusSearchRadius");
        private static readonly int FocusLerpId = Shader.PropertyToID("_FocusLerp");
        private static readonly int AutofocusMaxDistanceId = Shader.PropertyToID("_AutofocusMaxDistance");
        private static readonly int FocusRangeId = Shader.PropertyToID("_FocusRange");
        private static readonly int BlurFalloffId = Shader.PropertyToID("_BlurFalloff");
        private static readonly int MaxBlurRadiusId = Shader.PropertyToID("_MaxBlurRadius");
        private static readonly int ForegroundBlurScaleId = Shader.PropertyToID("_ForegroundBlurScale");
        private static readonly int ForegroundBlurFloorId = Shader.PropertyToID("_ForegroundBlurFloor");
        private static readonly int ForegroundDepthTexId = Shader.PropertyToID("_ForegroundDepthTex");
        private static readonly int MergedDepthTexId = Shader.PropertyToID("_MergedDepthTex");
        private static readonly int PrevFocusTexId = Shader.PropertyToID("_PrevFocusTex");
        private static readonly int FocusTexId = Shader.PropertyToID("_FocusTex");
        private static readonly int DofColorTexId = Shader.PropertyToID("_DofColorTex");
        private static readonly int DofBlurredTexId = Shader.PropertyToID("_DofBlurredTex");

        private Settings _settings;

        public NearFieldDepthOfFieldPass()
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        }

        public void Setup(Settings settings)
        {
            _settings = settings;
        }

        private class PassData
        {
            public Material material;
            public int shaderPass;

            public TextureHandle source;
            public TextureHandle foregroundDepth;
            public TextureHandle mergedDepth;
            public TextureHandle focusCurrent;
            public TextureHandle focusPrevious;
            public TextureHandle dofColor;
            public TextureHandle dofBlurred;

            public Vector4 screenSize;
            public Vector2 focusPoint;
            public float focusSearchRadius;
            public float focusLerp;
            public float autofocusMaxDistance;
            public float focusRange;
            public float blurFalloff;
            public float maxBlurRadius;
            public float foregroundBlurScale;
            public float foregroundBlurFloor;
            public float pinnedDistance;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            if (cameraData.camera == null)
                return;

            TextureHandle source = resourceData.activeColorTexture;
            TextureHandle worldDepth = resourceData.cameraDepthTexture;
            TextureHandle lensDepth = resourceData.activeDepthTexture;

            // The camera depth texture is the world as it stood after the opaque pass,
            // taken before the lens pass threw the attachment away. Without it there is
            // nothing to merge against and nothing to measure, so there is no effect to
            // apply -- Renderer -> Rendering -> Depth Texture Mode must not be Disabled.
            if (!source.IsValid() || !worldDepth.IsValid())
                return;

            bool merge = _settings.foregroundOnTop && lensDepth.IsValid();

            // Without a merge there is no foreground to exempt, so the exemption goes in
            // as neutral. The alternative -- binding the world's depth into the
            // foreground slot and letting the shader's test pass everywhere -- would apply
            // the held item's blur scale to the entire frame.
            TextureHandle foregroundDepth = merge ? lensDepth : worldDepth;
            float foregroundBlurScale = merge ? _settings.foregroundBlurScale : 1.0f;

            var sourceDesc = renderGraph.GetTextureDesc(source);

            int fullWidth = Mathf.Max(1, sourceDesc.width);
            int fullHeight = Mathf.Max(1, sourceDesc.height);

            var screenSize = new Vector4(fullWidth, fullHeight, 1.0f / fullWidth, 1.0f / fullHeight);

            // ── merged depth ────────────────────────────────────────────────────────
            var depthDesc = sourceDesc;
            depthDesc.name = "NearFieldMergedDepth";
            depthDesc.width = fullWidth;
            depthDesc.height = fullHeight;

            // Single channel, full float, point filtered. This holds a raw device depth
            // per pixel, and anything that interpolates two of them invents a surface
            // halfway between the barrel and the wall behind it.
            depthDesc.format = GraphicsFormat.R32_SFloat;
            depthDesc.depthBufferBits = 0;
            depthDesc.filterMode = FilterMode.Point;
            depthDesc.clearBuffer = false;
            depthDesc.msaaSamples = MSAASamples.None;

            var mergedDepth = renderGraph.CreateTexture(depthDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field Depth Merge", out var passData))
            {
                passData.material = _settings.material;
                passData.shaderPass = MergeShaderPass;
                passData.source = worldDepth;
                passData.foregroundDepth = foregroundDepth;

                builder.UseTexture(worldDepth, AccessFlags.Read);

                if (merge)
                    builder.UseTexture(lensDepth, AccessFlags.Read);

                builder.SetRenderAttachment(mergedDepth, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalTexture(ForegroundDepthTexId, data.foregroundDepth);
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                });
            }

            // ── focus ───────────────────────────────────────────────────────────────
            TextureHandle focusCurrent = renderGraph.ImportTexture(_settings.focusCurrent);
            TextureHandle focusPrevious = renderGraph.ImportTexture(_settings.focusPrevious);

            if (_settings.autofocus)
            {
                // MEASURED OFF THE MERGED BUFFER, so "what am I looking at" includes the
                // thing in my hands. That is the honest answer to the question, and the
                // held item stays sharp anyway because it is what focus lands on -- while
                // the distance gate is what stops it from being the answer forever: aim at
                // a wall three metres out and the wall wins, because it is nearer than the
                // resting distance and the item is not at the focus point.
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field Focus", out var passData))
                {
                    passData.material = _settings.material;
                    passData.shaderPass = FocusShaderPass;
                    passData.mergedDepth = mergedDepth;
                    passData.focusPrevious = focusPrevious;
                    passData.screenSize = screenSize;
                    passData.focusPoint = _settings.focusPoint;
                    passData.focusSearchRadius = _settings.focusSearchRadius;
                    passData.focusLerp = _settings.focusLerp;
                    passData.autofocusMaxDistance = _settings.autofocusMaxDistance;

                    builder.UseTexture(mergedDepth, AccessFlags.Read);
                    builder.UseTexture(focusPrevious, AccessFlags.Read);
                    builder.SetRenderAttachment(focusCurrent, 0, AccessFlags.Write);
                    builder.AllowGlobalStateModification(true);

                    builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                    {
                        data.material.SetVector(ScreenSizeId, data.screenSize);
                        data.material.SetVector(FocusPointId, data.focusPoint);
                        data.material.SetFloat(FocusSearchRadiusId, data.focusSearchRadius);
                        data.material.SetFloat(FocusLerpId, data.focusLerp);
                        data.material.SetFloat(AutofocusMaxDistanceId, data.autofocusMaxDistance);
                        ctx.cmd.SetGlobalTexture(MergedDepthTexId, data.mergedDepth);
                        ctx.cmd.SetGlobalTexture(PrevFocusTexId, data.focusPrevious);
                        Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                    });
                }
            }
            else
            {
                // Foreground and Manual both land here. The distance still goes into the
                // one-pixel texture rather than straight to a uniform, so the two passes
                // that read the focal distance read it from the same place whichever mode
                // is on -- a second route to the same number is a second thing to keep in
                // step.
                //
                // Written with a clear rather than a shader pass: the value is a constant,
                // the target is one pixel, and render graph has already bound it.
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field Focus (Pinned)", out var passData))
                {
                    passData.pinnedDistance = _settings.pinnedDistance;

                    builder.SetRenderAttachment(focusCurrent, 0, AccessFlags.Write);

                    // Green is 1: a pinned mode is the effect being asked for outright, so
                    // it is fully engaged. The gate is autofocus's business, and pinning
                    // focus is precisely the decision not to leave it to autofocus.
                    builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.ClearRenderTarget(false, true, new Color(data.pinnedDistance, 1.0f, 0.0f, 0.0f));
                    });
                }
            }

            // ── colour and circle of confusion, half resolution ─────────────────────
            int halfWidth = Mathf.Max(1, fullWidth / 2);
            int halfHeight = Mathf.Max(1, fullHeight / 2);

            var halfDesc = sourceDesc;
            halfDesc.width = halfWidth;
            halfDesc.height = halfHeight;
            halfDesc.name = "NearFieldDof_Colour";

            // Alpha carries the signed circle of confusion, so the format has to have one
            // and it has to be signed -- a unorm alpha clamps every foreground pixel's
            // negative radius to zero and the near field stops blurring.
            halfDesc.format = GraphicsFormat.R16G16B16A16_SFloat;
            halfDesc.depthBufferBits = 0;
            halfDesc.filterMode = FilterMode.Bilinear;
            halfDesc.clearBuffer = false;
            halfDesc.msaaSamples = MSAASamples.None;

            var dofColor = renderGraph.CreateTexture(halfDesc);

            halfDesc.name = "NearFieldDof_Blurred";
            var dofBlurred = renderGraph.CreateTexture(halfDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field Dof Prepare", out var passData))
            {
                passData.material = _settings.material;
                passData.shaderPass = PrepareShaderPass;
                passData.source = source;
                passData.mergedDepth = mergedDepth;
                passData.foregroundDepth = foregroundDepth;
                passData.focusCurrent = focusCurrent;
                passData.screenSize = screenSize;
                passData.focusRange = _settings.focusRange;
                passData.blurFalloff = _settings.blurFalloff;
                passData.maxBlurRadius = _settings.maxBlurRadius;
                passData.foregroundBlurScale = foregroundBlurScale;
                passData.foregroundBlurFloor = _settings.foregroundBlurFloor;

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(mergedDepth, AccessFlags.Read);
                builder.UseTexture(foregroundDepth, AccessFlags.Read);
                builder.UseTexture(focusCurrent, AccessFlags.Read);
                builder.SetRenderAttachment(dofColor, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    BindBlurState(data, ctx);
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                });
            }

            // ── gather ──────────────────────────────────────────────────────────────
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field Dof Gather", out var passData))
            {
                passData.material = _settings.material;
                passData.shaderPass = GatherShaderPass;
                passData.dofColor = dofColor;
                passData.screenSize = screenSize;

                builder.UseTexture(dofColor, AccessFlags.Read);
                builder.SetRenderAttachment(dofBlurred, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.material.SetVector(ScreenSizeId, data.screenSize);
                    ctx.cmd.SetGlobalTexture(DofColorTexId, data.dofColor);
                    Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                });
            }

            // ── composite ───────────────────────────────────────────────────────────
            var tempDesc = sourceDesc;
            tempDesc.name = "NearFieldDof_Temp";
            var tempTexture = renderGraph.CreateTexture(tempDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field Dof Composite", out var passData))
            {
                passData.material = _settings.material;
                passData.shaderPass = CompositeShaderPass;
                passData.source = source;
                passData.mergedDepth = mergedDepth;
                passData.foregroundDepth = foregroundDepth;
                passData.focusCurrent = focusCurrent;
                passData.dofBlurred = dofBlurred;
                passData.screenSize = screenSize;
                passData.focusRange = _settings.focusRange;
                passData.blurFalloff = _settings.blurFalloff;
                passData.maxBlurRadius = _settings.maxBlurRadius;
                passData.foregroundBlurScale = foregroundBlurScale;
                passData.foregroundBlurFloor = _settings.foregroundBlurFloor;

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(mergedDepth, AccessFlags.Read);
                builder.UseTexture(foregroundDepth, AccessFlags.Read);
                builder.UseTexture(focusCurrent, AccessFlags.Read);
                builder.UseTexture(dofBlurred, AccessFlags.Read);
                builder.SetRenderAttachment(tempTexture, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    BindBlurState(data, ctx);
                    ctx.cmd.SetGlobalTexture(DofBlurredTexId, data.dofBlurred);
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field Dof Copy Back", out var passData))
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

        // The prepare pass and the composite pass both evaluate the circle of confusion,
        // and they have to agree about it exactly -- the composite decides where the sharp
        // image survives and the prepare decides what the blurred one looks like, so a
        // difference between them shows as the held item being assembled from two
        // different pictures. One binder, called by both.
        private static void BindBlurState(PassData data, RasterGraphContext ctx)
        {
            data.material.SetVector(ScreenSizeId, data.screenSize);
            data.material.SetFloat(FocusRangeId, data.focusRange);
            data.material.SetFloat(BlurFalloffId, data.blurFalloff);
            data.material.SetFloat(MaxBlurRadiusId, data.maxBlurRadius);
            data.material.SetFloat(ForegroundBlurScaleId, data.foregroundBlurScale);
            data.material.SetFloat(ForegroundBlurFloorId, data.foregroundBlurFloor);

            ctx.cmd.SetGlobalTexture(MergedDepthTexId, data.mergedDepth);
            ctx.cmd.SetGlobalTexture(ForegroundDepthTexId, data.foregroundDepth);
            ctx.cmd.SetGlobalTexture(FocusTexId, data.focusCurrent);
        }
    }
}

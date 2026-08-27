using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

// Makes Layer "Player" exempt from URP's built-in SSAO and gives it its own
// custom occlusion instead (see PlayerAOVolume/PlayerAO.shader). Built-in
// URP SSAO has no per-layer exclusion of its own, so this works by
// overwriting the RESULT: right after SSAO's own pass, this feature marks
// which screen pixels are Player geometry, then rewrites the global
// _ScreenSpaceOcclusionTexture, replacing SSAO's value with a
// custom-computed one at Player pixels and leaving it untouched everywhere
// else. Every other object's lighting keeps sampling that same global
// texture the same way it always did, so nothing else needs to change.
public class PlayerAOFeature : ScriptableRendererFeature
{
    [SerializeField] private Shader _maskShader;
    [SerializeField] private Shader _aoShader;
    [SerializeField] private LayerMask _playerLayer = 0;

    private Material _maskMaterial;
    private Material _aoMaterial;
    private PlayerAOPass _pass;

    public override void Create()
    {
        if (_maskShader == null)
            _maskShader = Shader.Find("Hidden/PostProcessing/PlayerMask");
        if (_aoShader == null)
            _aoShader = Shader.Find("Hidden/PostProcessing/PlayerAO");

        if (_maskShader == null || _aoShader == null) return;

        _maskMaterial = CoreUtils.CreateEngineMaterial(_maskShader);
        _aoMaterial = CoreUtils.CreateEngineMaterial(_aoShader);

        if (_playerLayer == 0)
            _playerLayer = LayerMask.GetMask("Player");

        _pass = new PlayerAOPass(_maskMaterial, _aoMaterial, _playerLayer);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null || _maskMaterial == null || _aoMaterial == null) return;

        var stack = VolumeManager.instance.stack;
        var playerAO = stack.GetComponent<PlayerAOVolume>();
        if (playerAO == null || !playerAO.IsActive()) return;

        _pass.SetVolume(playerAO);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_maskMaterial);
        CoreUtils.Destroy(_aoMaterial);
    }

    // Both the mask draw and the AO composite happen inside ONE
    // RecordRenderGraph call, so the mask TextureHandle is just a local
    // variable passed straight from the first sub-pass to the second --
    // no static/shared state that could get crossed between cameras if
    // more than one renders through this feature in the same frame.
    class PlayerAOPass : ScriptableRenderPass
    {
        private readonly Material _maskMaterial;
        private readonly Material _aoMaterial;
        private readonly FilteringSettings _filteringSettings;
        private PlayerAOVolume _volume;

        private static readonly int PlayerMaskTexId = Shader.PropertyToID("_PlayerMaskTexture");
        private static readonly int RadiusId = Shader.PropertyToID("_PlayerAORadius");
        private static readonly int IntensityId = Shader.PropertyToID("_PlayerAOIntensity");
        private static readonly int PowerId = Shader.PropertyToID("_PlayerAOPower");
        private static readonly int OutlineIntensityId = Shader.PropertyToID("_PlayerAOOutlineIntensity");
        private static readonly int OutlineThicknessId = Shader.PropertyToID("_PlayerAOOutlineThickness");
        private static readonly int SsaoTextureId = Shader.PropertyToID("_ScreenSpaceOcclusionTexture");

        public PlayerAOPass(Material maskMaterial, Material aoMaterial, LayerMask playerLayer)
        {
            _maskMaterial = maskMaterial;
            _aoMaterial = aoMaterial;
            // Runs right after URP's own SSAO pass (AfterRenderingPrePasses + 1)
            // so it reads SSAO's freshly-set global texture, and well before
            // any opaque object samples that global for lighting.
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 2;
            requiresIntermediateTexture = true;
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, playerLayer);
        }

        public void SetVolume(PlayerAOVolume volume) => _volume = volume;

        private class MaskPassData
        {
            public RendererListHandle rendererList;
        }

        private class CompositePassData
        {
            public Material material;
            public TextureHandle playerMask;
            public float radius;
            public float intensity;
            public float power;
            public float outlineIntensity;
            public float outlineThickness;
        }

        private class BlurPassData
        {
            public Material material;
            public TextureHandle source;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_maskMaterial == null || _aoMaterial == null || _volume == null) return;

            var resourceData = frameData.Get<UniversalResourceData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            // ---- Sub-pass 1: mark which pixels are visible Player geometry ----
            var maskDesc = renderGraph.GetTextureDesc(resourceData.activeColorTexture);
            maskDesc.name = "_PlayerMaskTexture";
            maskDesc.clearBuffer = true;
            maskDesc.clearColor = Color.clear;
            maskDesc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8_UNorm;
            maskDesc.depthBufferBits = 0;

            TextureHandle maskTexture = renderGraph.CreateTexture(maskDesc);

            using (var builder = renderGraph.AddRasterRenderPass<MaskPassData>("Player AO Mask", out var passData))
            {
                var sortingSettings = new SortingSettings(cameraData.camera) { criteria = SortingCriteria.CommonOpaque };
                // Matched against the objects' own "UniversalForward" pass
                // tag purely for renderer eligibility -- overrideMaterial
                // below replaces what actually gets drawn with, so every
                // normal URP-shaded Player renderer (any of this project's
                // custom shaders, or stock Lit) is found and masked
                // regardless of which one it uses.
                var drawingSettings = new DrawingSettings(new ShaderTagId("UniversalForward"), sortingSettings)
                {
                    overrideMaterial = _maskMaterial,
                    overrideMaterialPassIndex = 0,
                    perObjectData = PerObjectData.None,
                };

                var param = new RendererListParams(renderingData.cullResults, drawingSettings, _filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(param);

                builder.UseRendererList(passData.rendererList);
                builder.SetRenderAttachment(maskTexture, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (MaskPassData data, RasterGraphContext ctx) =>
                {
                    ctx.cmd.DrawRendererList(data.rendererList);
                });
            }

            // ---- Sub-pass 2: composite custom AO over the mask, passthrough elsewhere ----
            var resultDesc = maskDesc;
            resultDesc.name = "_PlayerAOResultTexture";
            resultDesc.clearBuffer = false;

            TextureHandle resultTexture = renderGraph.CreateTexture(resultDesc);

            using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>("Player AO Composite", out var passData))
            {
                passData.material = _aoMaterial;
                passData.playerMask = maskTexture;
                passData.radius = _volume.radius.value;
                passData.intensity = _volume.intensity.value;
                passData.power = _volume.power.value;
                passData.outlineIntensity = _volume.outlineIntensity.value;
                passData.outlineThickness = _volume.outlineThickness.value;

                builder.UseTexture(maskTexture, AccessFlags.Read);
                builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                builder.UseTexture(resourceData.cameraNormalsTexture, AccessFlags.Read);
                builder.SetRenderAttachment(resultTexture, 0, AccessFlags.Write);
                // Needed because SetRenderFunc below calls
                // cmd.SetGlobalTexture (for _PlayerMaskTexture) -- RenderGraph
                // disallows global-state changes from a pass by default and
                // throws InvalidOperationException otherwise.
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (CompositePassData data, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalTexture(PlayerMaskTexId, data.playerMask);
                    data.material.SetFloat(RadiusId, data.radius);
                    data.material.SetFloat(IntensityId, data.intensity);
                    data.material.SetFloat(PowerId, data.power);
                    data.material.SetFloat(OutlineIntensityId, data.outlineIntensity);
                    data.material.SetFloat(OutlineThicknessId, data.outlineThickness);

                    // No source texture to blit FROM -- this pass only reads
                    // globals (_ScreenSpaceOcclusionTexture, depth, normals)
                    // plus the player mask, and writes a fresh result, so
                    // the no-source overload of BlitTexture is used (just
                    // draws a full-screen triangle with the material)
                    // instead of the copy-a-source-texture one.
                    Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), data.material, 0);
                });
            }

            // ---- Sub-pass 3: bilateral blur to remove sampling noise ----
            var blurredDesc = resultDesc;
            blurredDesc.name = "_PlayerAOBlurredTexture";

            TextureHandle blurredTexture = renderGraph.CreateTexture(blurredDesc);

            using (var builder = renderGraph.AddRasterRenderPass<BlurPassData>("Player AO Blur", out var passData))
            {
                passData.material = _aoMaterial;
                passData.source = resultTexture;

                builder.UseTexture(resultTexture, AccessFlags.Read);
                builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                builder.SetRenderAttachment(blurredTexture, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (BlurPassData data, RasterGraphContext ctx) =>
                {
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, 1);
                });

                builder.SetGlobalTextureAfterPass(blurredTexture, SsaoTextureId);
            }
        }
    }
}

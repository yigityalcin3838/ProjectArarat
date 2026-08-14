using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class ScreenSharpeningFeature : ScriptableRendererFeature
{
    [SerializeField] private Shader _shader;
    private Material _material;
    private ScreenSharpeningPass _pass;

    public override void Create()
    {
        if (_shader == null)
            _shader = Shader.Find("Hidden/PostProcessing/ScreenSharpening");

        if (_shader == null) return;

        _material = CoreUtils.CreateEngineMaterial(_shader);
        _pass = new ScreenSharpeningPass(_material);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null || _material == null) return;

        // Sadece post-processing aktif olan kamerada çalış
        if (!renderingData.cameraData.postProcessEnabled) return;

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
    }

    class ScreenSharpeningPass : ScriptableRenderPass
    {
        private Material _material;
        private static readonly int IntensityId = Shader.PropertyToID("_Intensity");

        public ScreenSharpeningPass(Material material)
        {
            _material = material;
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            requiresIntermediateTexture = true;
        }

        private class PassData
        {
            public Material material;
            public TextureHandle source;
            public float intensity;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var stack = VolumeManager.instance.stack;
            var sharpening = stack.GetComponent<ScreenSharpeningVolume>();

            if (sharpening == null || !sharpening.IsActive()) return;
            if (_material == null) return;

            var resourceData = frameData.Get<UniversalResourceData>();

            var source = resourceData.activeColorTexture;

            var desc = renderGraph.GetTextureDesc(source);
            desc.name = "_SharpeningTempRT";
            desc.clearBuffer = false;

            TextureHandle tempTexture = renderGraph.CreateTexture(desc);

            // Pass 1: Source -> Temp with sharpening material
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Screen Sharpening", out var passData))
            {
                passData.material = _material;
                passData.source = source;
                passData.intensity = sharpening.intensity.value;

                builder.UseTexture(source, AccessFlags.Read);
                builder.SetRenderAttachment(tempTexture, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.material.SetFloat(IntensityId, data.intensity);
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, 0);
                });
            }

            // Pass 2: Copy temp back to source
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Screen Sharpening Copy Back", out var passData))
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
    }
}

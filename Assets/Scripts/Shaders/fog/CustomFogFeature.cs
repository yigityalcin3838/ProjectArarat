using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class CustomFogFeature : ScriptableRendererFeature
{
    [SerializeField] private Shader _shader;
    private Material _material;
    private CustomFogPass _pass;

    public override void Create()
    {
        if (_shader == null)
            _shader = Shader.Find("Hidden/PostProcessing/CustomFog");

        if (_shader == null) return;

        _material = CoreUtils.CreateEngineMaterial(_shader);
        _pass = new CustomFogPass(_material);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null || _material == null) return;
        if (!renderingData.cameraData.postProcessEnabled) return;

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
    }

    class CustomFogPass : ScriptableRenderPass
    {
        private Material _material;

        private static readonly int FogColorId = Shader.PropertyToID("_FogColor");
        private static readonly int FogDensityId = Shader.PropertyToID("_FogDensity");
        private static readonly int FogHeightFalloffId = Shader.PropertyToID("_FogHeightFalloff");
        private static readonly int StartDistanceId = Shader.PropertyToID("_StartDistance");
        private static readonly int CutoffDistanceId = Shader.PropertyToID("_CutoffDistance");
        private static readonly int MaxOpacityId = Shader.PropertyToID("_MaxOpacity");

        public CustomFogPass(Material material)
        {
            _material = material;
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            requiresIntermediateTexture = true;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        private class PassData
        {
            public Material material;
            public TextureHandle source;
            public Color fogColor;
            public float density;
            public float heightFalloff;
            public float startDistance;
            public float cutoffDistance;
            public float maxOpacity;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var stack = VolumeManager.instance.stack;
            var fog = stack.GetComponent<CustomFogVolume>();

            if (fog == null || !fog.IsActive()) return;
            if (_material == null) return;

            var resourceData = frameData.Get<UniversalResourceData>();

            var source = resourceData.activeColorTexture;

            var desc = renderGraph.GetTextureDesc(source);
            desc.name = "_CustomFogTempRT";
            desc.clearBuffer = false;

            TextureHandle tempTexture = renderGraph.CreateTexture(desc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Custom Fog", out var passData))
            {
                passData.material = _material;
                passData.source = source;
                passData.fogColor = fog.fogColor.value;
                passData.density = fog.density.value;
                passData.heightFalloff = fog.heightFalloff.value;
                passData.startDistance = fog.startDistance.value;
                passData.cutoffDistance = fog.cutoffDistance.value;
                passData.maxOpacity = fog.maxOpacity.value;

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                builder.SetRenderAttachment(tempTexture, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.material.SetColor(FogColorId, data.fogColor);
                    data.material.SetFloat(FogDensityId, data.density);
                    data.material.SetFloat(FogHeightFalloffId, data.heightFalloff);
                    data.material.SetFloat(StartDistanceId, data.startDistance);
                    data.material.SetFloat(CutoffDistanceId, data.cutoffDistance);
                    data.material.SetFloat(MaxOpacityId, data.maxOpacity);
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, 0);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Custom Fog Copy Back", out var passData))
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

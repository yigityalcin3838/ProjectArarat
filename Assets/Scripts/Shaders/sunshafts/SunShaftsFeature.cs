using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class SunShaftsFeature : ScriptableRendererFeature
{
    [SerializeField] private Shader _shader;

    // One material per pass, NOT one shared between them. Setting a property
    // on a Material takes effect immediately on the CPU while Blitter only
    // RECORDS a draw that runs later, so a shared material would leave every
    // pass drawing with whatever the last one happened to set.
    private Material _scatterMaterial;
    private Material _compositeMaterial;
    private SunShaftsPass _pass;

    public override void Create()
    {
        if (_shader == null)
            _shader = Shader.Find("Hidden/PostProcessing/SunShafts");

        if (_shader == null) return;

        _scatterMaterial = CoreUtils.CreateEngineMaterial(_shader);
        _compositeMaterial = CoreUtils.CreateEngineMaterial(_shader);
        _pass = new SunShaftsPass(_scatterMaterial, _compositeMaterial);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null || _scatterMaterial == null) return;
        if (!renderingData.cameraData.postProcessEnabled) return;

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_scatterMaterial);
        CoreUtils.Destroy(_compositeMaterial);
    }

    class SunShaftsPass : ScriptableRenderPass
    {
        private const int ScatterPass = 0;
        private const int CompositePass = 1;

        private readonly Material _scatterMaterial;
        private readonly Material _compositeMaterial;

        private static readonly int DensityId = Shader.PropertyToID("_Density");
        private static readonly int ForwardScatteringId = Shader.PropertyToID("_ForwardScattering");
        private static readonly int MaxForwardBoostId = Shader.PropertyToID("_MaxForwardBoost");
        private static readonly int MaxDistanceId = Shader.PropertyToID("_MaxDistance");
        private static readonly int StepsId = Shader.PropertyToID("_Steps");
        private static readonly int ShaftColorId = Shader.PropertyToID("_ShaftColor");
        private static readonly int IntensityId = Shader.PropertyToID("_Intensity");

        public SunShaftsPass(Material scatterMaterial, Material compositeMaterial)
        {
            _scatterMaterial = scatterMaterial;
            _compositeMaterial = compositeMaterial;

            // Before post-processing, so tonemapping and bloom treat the
            // shafts as part of the image rather than something pasted on top.
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            requiresIntermediateTexture = true;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        private class PassData
        {
            public Material material;
            public TextureHandle source;
            public int shaderPass;

            public float density;
            public float forwardScattering;
            public float maxForwardBoost;
            public float maxDistance;
            public int steps;
            public Color shaftColor;
            public float intensity;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_scatterMaterial == null || _compositeMaterial == null) return;

            var stack = VolumeManager.instance.stack;
            var settings = stack.GetComponent<SunShaftsVolume>();
            if (settings == null || !settings.IsActive()) return;

            // Nothing to scatter without a directional light casting shadows --
            // the shadow map is the only thing that says where the beams are.
            Light sun = RenderSettings.sun;
            if (sun == null || !sun.isActiveAndEnabled || sun.type != LightType.Directional)
                return;

            var resourceData = frameData.Get<UniversalResourceData>();
            TextureHandle source = resourceData.activeColorTexture;

            int divider = Mathf.Max(1, settings.downsample.value);
            var desc = renderGraph.GetTextureDesc(source);
            desc.name = "_SunShaftsScattering";
            desc.width = Mathf.Max(1, desc.width / divider);
            desc.height = Mathf.Max(1, desc.height / divider);
            desc.clearBuffer = false;
            desc.depthBufferBits = 0;

            TextureHandle scattering = renderGraph.CreateTexture(desc);

            // 1. March each ray through the shadow map and record how much lit
            //    air it passed through.
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Sun Shafts Scatter", out var passData))
            {
                passData.material = _scatterMaterial;
                passData.source = source;
                passData.shaderPass = ScatterPass;
                passData.density = settings.density.value;
                passData.forwardScattering = settings.forwardScattering.value;
                passData.maxForwardBoost = settings.maxForwardBoost.value;
                passData.maxDistance = settings.maxDistance.value;
                passData.steps = settings.steps.value;

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                builder.SetRenderAttachment(scattering, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) => Execute(data, ctx));
            }

            // 2. Add it over the frame.
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Sun Shafts Composite", out var passData))
            {
                passData.material = _compositeMaterial;
                passData.source = scattering;
                passData.shaderPass = CompositePass;
                passData.shaftColor = settings.shaftColor.value;
                passData.intensity = settings.intensity.value;

                builder.UseTexture(scattering, AccessFlags.Read);
                builder.SetRenderAttachment(source, 0, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) => Execute(data, ctx));
            }
        }

        private static void Execute(PassData data, RasterGraphContext ctx)
        {
            data.material.SetFloat(DensityId, data.density);
            data.material.SetFloat(ForwardScatteringId, data.forwardScattering);
            data.material.SetFloat(MaxForwardBoostId, data.maxForwardBoost);
            data.material.SetFloat(MaxDistanceId, data.maxDistance);
            data.material.SetInt(StepsId, data.steps);
            data.material.SetColor(ShaftColorId, data.shaftColor);
            data.material.SetFloat(IntensityId, data.intensity);

            Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
        }
    }
}

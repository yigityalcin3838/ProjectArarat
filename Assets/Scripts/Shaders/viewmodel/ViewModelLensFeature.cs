using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

// Everything that has to be true about the things held up against the lens -- the item
// in hand and the player's own body -- because none of it is true by default.
//
// Three passes, in this order:
//
//   1. occlusion for the body
//   2. the held item, drawn through a lens of its own
//   3. occlusion for the held item
//
// They are in one feature because the lens field of view is authored once, here, and
// both the draw and the occlusion have to unproject with it. Split across two renderer
// features that would be two copies of the same number, and the symptom of them
// disagreeing is occlusion sitting next to the weapon it belongs to rather than on it.
//
// ── THE LENS ─────────────────────────────────────────────────────────────────────────
//
// One job: take the view-model layer out of the ordinary opaque pass and draw it again
// with a different projection, so a weapon can be shown at a narrower field of view
// than the world without being a separate camera.
//
// A separate camera was tried and gives a true lens, but it renders after the world's
// post-processing, so the weapon comes out ungraded -- and putting post on the second
// camera runs the whole chain twice. One camera, one post, one grade; the lens is the
// only thing that differs, and it differs here.
//
// A narrower lens magnifies, by tan(world/2) / tan(lens/2). Do not try to buy the size
// back by moving the item away from the camera -- the body is full and its hands are IK'd
// to the grip points, so the item's position belongs to the arms.
//
// AND KNOW WHAT THE LENS COSTS ON A FULL BODY: the hand is body, drawn at the camera's
// field of view, and the grip is item, drawn at this one. Two matrices, one contact
// point, so they line up at one depth and only by arrangement. Setting the lens to 0
// puts everything back on one projection and is the honest choice for a full body; the
// lens is worth its cost only if the weapon's splay at a wide world FOV is worse to look
// at than a hand that sits slightly off its grip.
//
// ── THE DEPTH CLEAR ──────────────────────────────────────────────────────────────────
//
// Not about wall clipping. It is what makes a second lens coherent at all.
//
// A weapon drawn through a 35 degree matrix, depth-tested against a buffer a 70 degree
// matrix filled in, asks one depth buffer to arbitrate between two lenses. The pixels do
// not correspond, and the symptom is the weapon being cut into by geometry nowhere near
// it -- you end up looking at the inside of the receiver. This was removed once, on the
// reasoning that the clear only existed to stop the weapon burying itself in walls and
// the weapon already retracts near walls. The retraction is real; the clear is still
// needed, because separating the two lenses was its other job and the one that cannot be
// done anywhere else.
//
// The clear is safe HERE and would not be earlier: _CameraDepthTexture has already been
// copied by then (Renderer -> Rendering -> Depth Texture Mode: After Opaques), so post
// still has the world's depth to read.
//
// ── SETUP, and all three parts are needed ────────────────────────────────────────────
//
//   * Renderer (PC_Renderer) -> Filtering -> Opaque and Transparent Layer Mask:
//     EXCLUDE the view-model layer. That is what stops the ordinary pass drawing it.
//   * Renderer -> Filtering -> Prepass Layer Mask: EXCLUDE it as well, or the
//     depth-normals prepass draws the weapon through the CAMERA's projection and SSAO
//     multiplies that ghost's occlusion over the world. (URP 17.5 gave the prepass its
//     own mask; before that it followed the opaque mask.)
//   * Camera -> Culling Mask: INCLUDE it. Objects have to survive culling to be in the
//     results these passes draw from.
public class ViewModelLensFeature : ScriptableRendererFeature
{
    [Header("Held item lens")]
    [Tooltip("The layer held items are moved onto while in hand. See GameLayers.")]
    [SerializeField] private LayerMask viewModelLayers = 0;

    // One figure for every held item, authored here and nowhere else. There was a
    // per-weapon version of this, pushed in through a static, and an aim figure
    // alongside it. Both are gone: one lens is what the setting is for, and a value that
    // could be written from three places was three places to look when the weapon came
    // out wrong.
    //
    // 0 means "the camera's own", and the item is still drawn and still overlaid -- the
    // lens is the only thing that switches off. It has to work that way round: the
    // ordinary pass no longer draws this layer, so a pass that skipped itself here would
    // take the weapon off the screen entirely.
    [Tooltip("Field of view to draw held items at, in degrees. 0 uses the camera's own.")]
    [SerializeField] private float viewModelFov = 35.0f;

    // ── Occlusion ────────────────────────────────────────────────────────────────────
    //
    // WHY THESE TWO GET THEIR OWN AO. URP's SSAO is one radius quoted for a room, and
    // these two fail it in different ways. The held item is drawn through a lens of its
    // own, so it is not in the world's depth-normals at all and URP has nothing to work
    // from. The body IS in there, but its creases -- under the chin, arm against torso,
    // inside the elbow -- are finer than a room-sized radius can resolve.
    //
    // Each group renders its own depth and normals, through its own matrix, and the
    // occlusion is computed from those. That is why they are two runs of the same three
    // passes rather than one: the item's matrix is the lens, the body's is the camera's,
    // and one unprojection cannot serve both.
    [Header("Near-field ambient occlusion")]
    [Tooltip("Hidden/PostProcessing/NearFieldAO. Assign NearFieldAO.shader.")]
    [SerializeField] private Shader nearFieldAOShader;

    [Tooltip("Occlusion for the held item.")]
    [SerializeField] private bool occludeHeldItem = true;

    // The body is drawn by the ordinary opaque pass, so it is ALSO reached by URP's
    // SSAO. The two add up. That is tunable rather than wrong -- this pass is the tight
    // contact shading SSAO cannot see -- but if the body ends up too dark, the intensity
    // below is the knob to bring down, not URP's.
    [Tooltip("Layers the player's own body is on. Empty turns body occlusion off.")]
    [SerializeField] private LayerMask bodyLayers = 0;

    // METRES, and that is the point of this version. Authored in pixels, the same
    // physical crease darkened by a different amount depending on how much of the screen
    // it covered -- it changed with distance and it changed with field of view. A world
    // radius is the same crease at any distance, which is what consistency has to mean
    // on a surface with fine detail.
    //
    // MILLIMETRES, THOUGH, NOT CENTIMETRES, and the first version of this field said 5 cm
    // on the reasoning that it was "the width of a trigger guard". The reasoning left out
    // how close the subject is. A weapon 30 cm from the eye through a 35 degree lens sees
    // 2*0.30*tan(17.5) = 0.19 m of world across the screen's height, so 5 cm of radius is
    // a quarter of the screen -- twenty-four samples over that area produce blotches, and
    // a five-tap blur cannot reach across something hundreds of pixels wide. Which is
    // also why raising the blur sharpness appeared to do nothing: the blur was working,
    // at entirely the wrong scale.
    [Tooltip("Sampling radius in METRES. 0.012 is a good start; this is a near-field " +
             "effect, so think millimetres to a centimetre, not the 0.3 m a room wants.")]
    [Range(0.002f, 0.2f)]
    [SerializeField] private float aoRadius = 0.012f;

    [Range(0.0f, 4.0f)]
    [SerializeField] private float aoIntensity = 1.0f;

    // URP calls this kBeta and keeps it at 0.002. It is multiplied by the surface's own
    // depth, so it defends against depth precision the way depth precision behaves.
    [Tooltip("Self-shadow bias. Raise it if flat surfaces darken themselves.")]
    [Range(0.0f, 0.05f)]
    [SerializeField] private float aoBias = 0.002f;

    // How readily the blur refuses a neighbour that disagrees about depth, as a multiple
    // of relative depth difference. Higher keeps occlusion off the feature next door;
    // low enough and the blur stops being geometry-aware at all.
    [Tooltip("Higher keeps occlusion from crossing edges. 8 is a reasonable default.")]
    [Range(0.0f, 64.0f)]
    [SerializeField] private float blurDepthSharpness = 8.0f;

    // ── Spill ────────────────────────────────────────────────────────────────────────
    //
    // The subject darkening the surface behind it. One-sided, multiplied, and falling off
    // from the silhouette outward -- the three things that separate it from an outline.
    // It never touches a pixel the subject occupies, so the subject's own occlusion is
    // the only thing on it.
    //
    // PIXELS, and unlike the occlusion radius that is the right unit here. A true
    // world-space version would be invisible: the item is 30 cm from the eye and the room
    // is metres away, so nothing is within centimetres of it and there is no contact to
    // shade. This is a screen-space effect that seats the item in the frame, and the item
    // is rigid relative to the camera, so pixels and metres are proportional for it.
    [Header("Spill")]
    // Realised as the reach of a Gaussian over the subject mask, not as a sampling
    // radius -- so this is genuinely how many pixels past the silhouette the darkening
    // extends, and the conversion to a blur step is done once where the buffer sizes are
    // known.
    [Tooltip("How far the darkening reaches past the silhouette, in pixels.")]
    [Range(4.0f, 240.0f)]
    [SerializeField] private float spillRadius = 60.0f;

    // The same blurred mask read from both ends: outward from the silhouette into the
    // world, inward from the silhouette onto the subject. One radius, two strengths --
    // equal values give a symmetric band, which is an outline, so the asymmetry is where
    // this stops being one. Both zero skips the whole chain.
    [Tooltip("Darkening on the world just outside the subject.")]
    [Range(0.0f, 1.0f)]
    [SerializeField] private float spillOutwardIntensity = 0.35f;

    [Tooltip("Darkening on the subject just inside its own silhouette. Reaches inward " +
             "only as far as the part is thick -- on something thinner than the radius " +
             "it covers the whole part rather than hugging the edge.")]
    [Range(0.0f, 1.0f)]
    [SerializeField] private float spillInwardIntensity = 0.0f;

    private ViewModelLensPass _lensPass;
    private NearFieldAOPass _heldItemAOPass;
    private NearFieldAOPass _bodyAOPass;

    // One set per group, and this is not tidiness. Material properties are state on the
    // material object, while a render pass's function runs later -- so two passes sharing
    // a material both read whichever values were written last. The two groups differ in
    // exactly the properties that matter (the projection, the radius), and the two blur
    // directions differ in _BlurStep, so each needs its own.
    private AOMaterials _heldItemMaterials;
    private AOMaterials _bodyMaterials;

    // Five, and every one of them is a separate object because _BlurStep differs between
    // them. Three of these blur, in two directions, at two resolutions -- four distinct
    // step values that all live under one property name.
    private class AOMaterials
    {
        public Material main;
        public Material blurHorizontal;
        public Material blurVertical;
        public Material spillBlurHorizontal;
        public Material spillBlurVertical;

        public bool IsValid =>
            main != null &&
            blurHorizontal != null && blurVertical != null &&
            spillBlurHorizontal != null && spillBlurVertical != null;

        public static AOMaterials Create(Shader shader)
        {
            if (shader == null)
                return null;

            return new AOMaterials
            {
                main = CoreUtils.CreateEngineMaterial(shader),
                blurHorizontal = CoreUtils.CreateEngineMaterial(shader),
                blurVertical = CoreUtils.CreateEngineMaterial(shader),
                spillBlurHorizontal = CoreUtils.CreateEngineMaterial(shader),
                spillBlurVertical = CoreUtils.CreateEngineMaterial(shader),
            };
        }

        public void Destroy()
        {
            CoreUtils.Destroy(main);
            CoreUtils.Destroy(blurHorizontal);
            CoreUtils.Destroy(blurVertical);
            CoreUtils.Destroy(spillBlurHorizontal);
            CoreUtils.Destroy(spillBlurVertical);

            main = null;
            blurHorizontal = null;
            blurVertical = null;
            spillBlurHorizontal = null;
            spillBlurVertical = null;
        }
    }

    public override void Create()
    {
        _lensPass = new ViewModelLensPass();
        _heldItemAOPass = new NearFieldAOPass();
        _bodyAOPass = new NearFieldAOPass();

        _heldItemMaterials?.Destroy();
        _bodyMaterials?.Destroy();

        _heldItemMaterials = AOMaterials.Create(nearFieldAOShader);
        _bodyMaterials = AOMaterials.Create(nearFieldAOShader);
    }

    protected override void Dispose(bool disposing)
    {
        _heldItemMaterials?.Destroy();
        _bodyMaterials?.Destroy();

        _heldItemMaterials = null;
        _bodyMaterials = null;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_lensPass == null || viewModelLayers == 0)
            return;

        CameraType cameraType = renderingData.cameraData.cameraType;

        // A reflection probe has no held item worth reprojecting. A scene view does have
        // one worth SEEING, though -- the layer is out of the ordinary pass, so without
        // this the item is invisible while placing grip points. It is drawn there as part
        // of the world instead: the camera's own projection, into the world's depth,
        // behind whatever is actually in front of it. One is a first-person view, the
        // other is a workspace.
        bool isOverlay = cameraType == CameraType.Game;

        if (!isOverlay && cameraType != CameraType.SceneView)
            return;

        // Occlusion is a first-person effect. The scene view draws these objects in the
        // world at the world's projection, where the world's own occlusion is the right
        // occlusion.
        if (!isOverlay)
        {
            _lensPass.Setup(viewModelLayers, viewModelFov, asOverlay: false);
            renderer.EnqueuePass(_lensPass);
            return;
        }

        var settings = new NearFieldAOPass.Settings
        {
            radius = aoRadius,
            intensity = aoIntensity,
            bias = aoBias,
            blurDepthSharpness = blurDepthSharpness,
            spillRadius = spillRadius,
            spillOutward = spillOutwardIntensity,
            spillInward = spillInwardIntensity,
        };

        // THE HELD ITEM IS TAKEN OUT OF THE BODY GROUP RATHER THAN TRUSTED TO BE ABSENT.
        //
        // The two groups exist because they are drawn through different matrices, so an
        // object in both is drawn through one and occluded through the other -- a
        // weapon-shaped smudge of occlusion sitting where the camera's matrix would have
        // put the weapon, next to the weapon the lens actually drew. Which is what
        // happens the moment View Model is ticked in Body Layers.
        //
        // Derived from viewModelLayers instead of relying on the two fields agreeing.
        // Which layer is the held item's is already answered above, and answering it
        // twice is how it gets answered differently.
        LayerMask bodySubject = bodyLayers & ~viewModelLayers;

        // ORDER. The body first, while the frame still shows the body: it composites onto
        // the colour, and after the lens pass some of those pixels are weapon.
        if (bodySubject != 0 && _bodyMaterials != null && _bodyMaterials.IsValid)
        {
            _bodyAOPass.Setup(_bodyMaterials, settings, 0.0f, bodySubject);
            renderer.EnqueuePass(_bodyAOPass);
        }

        _lensPass.Setup(viewModelLayers, viewModelFov, asOverlay: true);
        renderer.EnqueuePass(_lensPass);

        // The held item last, and it has to be last: its occlusion multiplies into the
        // pixels the weapon occupies, so running it before the weapon is drawn would
        // darken the world and then have the weapon painted over the result.
        if (occludeHeldItem && _heldItemMaterials != null && _heldItemMaterials.IsValid)
        {
            _heldItemAOPass.Setup(_heldItemMaterials, settings, viewModelFov, viewModelLayers);
            renderer.EnqueuePass(_heldItemAOPass);
        }
    }

    // The lens, in one place, so the draw and the two unprojections cannot disagree.
    private static bool TryGetLens(Camera camera, float fov, out Matrix4x4 projection)
    {
        projection = Matrix4x4.identity;

        if (fov <= 0.0f || Mathf.Abs(fov - camera.fieldOfView) < 0.01f)
            return false;

        // The camera's own projection with one number changed: same near and far planes,
        // same aspect, so the field of view is the only thing this alters.
        //
        // Left in Unity's convention on purpose. SetViewProjectionMatrices takes it that
        // way and does the platform conversion itself -- running it through
        // GL.GetGPUProjectionMatrix first converts it twice, which flips Y and puts the
        // weapon in the wrong half of the screen.
        projection = Matrix4x4.Perspective(
            fov, camera.aspect, camera.nearClipPlane, camera.farClipPlane);

        return true;
    }

    // tan(fov/2)*aspect, tan(fov/2), for the same field of view the draw used.
    private static Vector2 TanHalf(Camera camera, float fov)
    {
        float effective = fov > 0.0f ? fov : camera.fieldOfView;
        float tanHalfY = Mathf.Tan(effective * 0.5f * Mathf.Deg2Rad);

        return new Vector2(tanHalfY * camera.aspect, tanHalfY);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // The held item, through its own lens, over the world
    // ─────────────────────────────────────────────────────────────────────────────────
    private class ViewModelLensPass : ScriptableRenderPass
    {
        // UniversalForward for the forward and Forward+ paths, UniversalForwardOnly for
        // materials that only declare that one, SRPDefaultUnlit for anything unlit on
        // the layer. (This renderer is Forward+, not Deferred -- URP numbers
        // RenderingMode.ForwardPlus as 2 and Deferred as 1, which is easy to misread in
        // the asset.)
        private static readonly List<ShaderTagId> ShaderTags = new List<ShaderTagId>
        {
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly"),
            new ShaderTagId("SRPDefaultUnlit"),
        };

        // Built by name rather than taken from URP's own ShaderGlobalKeywords, which is
        // internal to the package. The name is the one the project's lit shaders declare
        // in their multi_compile.
        private static readonly GlobalKeyword ScreenSpaceOcclusion =
            GlobalKeyword.Create("_SCREEN_SPACE_OCCLUSION");

        private LayerMask _layers;
        private float _fov;
        private bool _asOverlay;

        public ViewModelLensPass()
        {
            // Last thing before post, which buys three separate things and each of them
            // is load-bearing:
            //   * the depth copy has already been taken, so clearing depth here does not
            //     rob post of the world's depth;
            //   * URP's after-opaque SSAO multiply has already landed, so the item
            //     cannot be darkened by the background's occlusion;
            //   * it is still before post, so the item is colour graded with everything
            //     else -- which is the reason this is a pass and not a second camera.
            //
            // Depth is cleared, so being after the transparent pass costs nothing: the
            // item would draw over transparents either way.
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        }

        public void Setup(LayerMask layers, float fov, bool asOverlay)
        {
            _layers = layers;
            _fov = fov;
            _asOverlay = asOverlay;
        }

        private class PassData
        {
            public RendererListHandle rendererList;
            public Matrix4x4 view;
            public Matrix4x4 projection;
            public Matrix4x4 restoreProjection;
            public bool asOverlay;
            public bool useLens;
            public bool occlusionWasEnabled;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            Camera camera = cameraData.camera;

            if (camera == null)
                return;

            Matrix4x4 view = cameraData.GetViewMatrix();
            Matrix4x4 cameraProjection = cameraData.GetProjectionMatrix();

            // A lens only in the game view, and only when one was actually asked for.
            // Called unconditionally rather than behind the && -- short-circuiting it
            // would leave the matrix unassigned, which the compiler will not have.
            bool hasLens = TryGetLens(camera, _fov, out Matrix4x4 lens);
            bool useLens = _asOverlay && hasLens;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("View Model Lens", out var passData))
            {
                var drawSettings = RenderingUtils.CreateDrawingSettings(
                    ShaderTags, renderingData, cameraData, lightData, SortingCriteria.CommonOpaque);

                var filterSettings = new FilteringSettings(RenderQueueRange.all, _layers);

                passData.rendererList = renderGraph.CreateRendererList(
                    new RendererListParams(renderingData.cullResults, drawSettings, filterSettings));

                passData.view = view;
                passData.projection = useLens ? lens : cameraProjection;
                passData.restoreProjection = cameraProjection;
                passData.asOverlay = _asOverlay;
                passData.useLens = useLens;
                passData.occlusionWasEnabled = Shader.IsKeywordEnabled(ScreenSpaceOcclusion);

                builder.UseRendererList(passData.rendererList);

                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);

                // Write when the buffer is about to be thrown away, ReadWrite in the
                // scene view where the item belongs behind what is in front of it.
                builder.SetRenderAttachmentDepth(
                    resourceData.activeDepthTexture,
                    _asOverlay ? AccessFlags.Write : AccessFlags.ReadWrite);

                // The matrices and the occlusion keyword are both global render state.
                // Declared, so render graph does not merge this pass into a neighbour and
                // leave either one applied to geometry that was never meant to see it --
                // and because it refuses global changes from a pass that has not said it
                // makes them.
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    if (data.asOverlay)
                    {
                        // Depth only, colour kept: the world's picture stays, its depth
                        // does not. From here the item is the only thing in the buffer,
                        // so it occludes itself correctly -- a barrel in front of a
                        // stock -- and nothing else gets a say.
                        ctx.cmd.ClearRenderTarget(true, false, Color.clear);

                        // The world's occlusion, off for this draw. With SSAO set to
                        // After Opaque the composite has already happened and this
                        // changes nothing; with it off, every lit shader darkens itself
                        // from the occlusion texture at its own screen position, and
                        // that texture knows nothing about a weapon -- so the weapon
                        // would wear the room's AO painted across the receiver. The pass
                        // after this one gives it occlusion of its own instead.
                        ctx.cmd.SetKeyword(ScreenSpaceOcclusion, false);
                    }

                    if (data.useLens)
                        ctx.cmd.SetViewProjectionMatrices(data.view, data.projection);

                    ctx.cmd.DrawRendererList(data.rendererList);

                    // Put the matrices and the keyword back. Everything after this is
                    // entitled to find them as it left them, and a pass that quietly
                    // changes global state is the kind of thing that breaks something
                    // three passes later.
                    if (data.useLens)
                        ctx.cmd.SetViewProjectionMatrices(data.view, data.restoreProjection);

                    if (data.asOverlay)
                        ctx.cmd.SetKeyword(ScreenSpaceOcclusion, data.occlusionWasEnabled);
                });
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // Occlusion for one subject
    //
    // Render the subject's depth and normals through its own matrix, compute occlusion
    // at half resolution, blur it across and down with a depth-aware filter, multiply it
    // over the colour. Instanced twice; everything that differs is in Setup.
    // ─────────────────────────────────────────────────────────────────────────────────
    private class NearFieldAOPass : ScriptableRenderPass
    {
        public struct Settings
        {
            public float radius;
            public float intensity;
            public float bias;
            public float blurDepthSharpness;
            public float spillRadius;
            public float spillOutward;
            public float spillInward;
        }

        private const int ComputeShaderPass = 0;
        private const int BlurShaderPass = 1;
        private const int CompositeShaderPass = 2;
        private const int SpillMaskShaderPass = 3;
        private const int SpillBlurShaderPass = 4;

        private static readonly int RadiusId = Shader.PropertyToID("_AORadius");
        private static readonly int IntensityId = Shader.PropertyToID("_AOIntensity");
        private static readonly int BiasId = Shader.PropertyToID("_AOBias");
        private static readonly int SpillOutwardId = Shader.PropertyToID("_SpillOutwardIntensity");
        private static readonly int SpillInwardId = Shader.PropertyToID("_SpillInwardIntensity");
        private static readonly int SpillTexId = Shader.PropertyToID("_SpillTex");
        private static readonly int ScreenSizeId = Shader.PropertyToID("_AOScreenSize");
        private static readonly int TanHalfId = Shader.PropertyToID("_AOTanHalf");
        private static readonly int BlurStepId = Shader.PropertyToID("_BlurStep");
        private static readonly int BlurSharpnessId = Shader.PropertyToID("_BlurDepthSharpness");
        private static readonly int AOTexId = Shader.PropertyToID("_AOTex");
        private static readonly int SubjectDepthTexId = Shader.PropertyToID("_SubjectDepthTex");
        private static readonly int SubjectNormalsTexId = Shader.PropertyToID("_SubjectNormalsTex");

        // The tags URP's own depth-normals prepass draws with, so any material that
        // works in that prepass works here. A subject whose shader declares neither
        // simply will not be in the buffers, and so will not be occluded -- the same way
        // it would not be in URP's prepass.
        private static readonly List<ShaderTagId> DepthNormalsTags = new List<ShaderTagId>
        {
            new ShaderTagId("DepthNormals"),
            new ShaderTagId("DepthNormalsOnly"),
        };

        private AOMaterials _materials;
        private Settings _settings;
        private float _fov;
        private LayerMask _subjectLayers;

        public NearFieldAOPass()
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        }

        // fov: the field of view the subject was drawn at, 0 for the camera's own.
        public void Setup(AOMaterials materials, Settings settings, float fov, LayerMask subjectLayers)
        {
            _materials = materials;
            _settings = settings;
            _fov = fov;
            _subjectLayers = subjectLayers;
        }

        private class SubjectPassData
        {
            public RendererListHandle rendererList;
            public Matrix4x4 view;
            public Matrix4x4 projection;
            public Matrix4x4 restoreProjection;
            public bool useLens;
        }

        private class PassData
        {
            public Material material;
            public TextureHandle source;
            public TextureHandle subjectDepth;
            public TextureHandle subjectNormals;
            public TextureHandle aoTexture;
            public TextureHandle spillTexture;
            public bool hasSpill;
            public int shaderPass;

            public Vector4 screenSize;
            public Vector2 tanHalf;
            public float radius;
            public float intensity;
            public float bias;
            public float spillOutward;
            public float spillInward;
            public Vector2 blurStep;
            public float blurSharpness;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            Camera camera = cameraData.camera;

            if (camera == null || _materials == null || !_materials.IsValid)
                return;

            bool wantsOcclusion = _settings.intensity > 0.0f && _settings.radius > 0.0f;
            bool wantsSpill = (_settings.spillOutward > 0.0f || _settings.spillInward > 0.0f)
                              && _settings.spillRadius > 0.0f;

            if (_subjectLayers == 0 || (!wantsOcclusion && !wantsSpill))
                return;

            TextureHandle source = resourceData.activeColorTexture;
            TextureHandle activeDepth = resourceData.activeDepthTexture;

            if (!source.IsValid() || !activeDepth.IsValid())
                return;

            var sourceDesc = renderGraph.GetTextureDesc(source);

            int fullWidth = Mathf.Max(1, sourceDesc.width);
            int fullHeight = Mathf.Max(1, sourceDesc.height);

            // ── the subject's own geometry ───────────────────────────────────────────
            //
            // Rendered rather than borrowed. The held item's depth is in fact sitting in
            // the camera's depth buffer by this point -- the lens pass cleared it and
            // drew only the item -- but its NORMALS are nowhere, and reconstructing them
            // from depth is exactly what made the first version of this crawl on detailed
            // surfaces. One render gives both, and it makes the two groups identical
            // apart from their matrix.
            var depthDesc = renderGraph.GetTextureDesc(activeDepth);
            depthDesc.name = "NearFieldAO_SubjectDepth";
            depthDesc.clearBuffer = false;
            depthDesc.msaaSamples = MSAASamples.None;
            depthDesc.filterMode = FilterMode.Point;

            var subjectDepth = renderGraph.CreateTexture(depthDesc);

            var normalsDesc = sourceDesc;
            normalsDesc.name = "NearFieldAO_SubjectNormals";
            normalsDesc.width = fullWidth;
            normalsDesc.height = fullHeight;

            // Signed and floating point, because these are world normals straight out of
            // the prepass shaders with no encoding applied. A unorm format would clamp
            // every negative component to zero and tilt half the surfaces.
            normalsDesc.format = GraphicsFormat.R16G16B16A16_SFloat;
            normalsDesc.depthBufferBits = 0;
            normalsDesc.filterMode = FilterMode.Point;
            normalsDesc.clearBuffer = true;
            normalsDesc.clearColor = Color.clear;
            normalsDesc.msaaSamples = MSAASamples.None;

            var subjectNormals = renderGraph.CreateTexture(normalsDesc);

            bool useLens = TryGetLens(camera, _fov, out Matrix4x4 lens);
            Matrix4x4 view = cameraData.GetViewMatrix();
            Matrix4x4 cameraProjection = cameraData.GetProjectionMatrix();

            using (var builder = renderGraph.AddRasterRenderPass<SubjectPassData>("Near Field AO Subject", out var passData))
            {
                var drawSettings = RenderingUtils.CreateDrawingSettings(
                    DepthNormalsTags, renderingData, cameraData, lightData, SortingCriteria.CommonOpaque);

                var filterSettings = new FilteringSettings(RenderQueueRange.opaque, _subjectLayers);

                passData.rendererList = renderGraph.CreateRendererList(
                    new RendererListParams(renderingData.cullResults, drawSettings, filterSettings));

                passData.view = view;
                passData.projection = useLens ? lens : cameraProjection;
                passData.restoreProjection = cameraProjection;
                passData.useLens = useLens;

                builder.UseRendererList(passData.rendererList);
                builder.SetRenderAttachment(subjectNormals, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(subjectDepth, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (SubjectPassData data, RasterGraphContext ctx) =>
                {
                    // Cleared the same way the lens pass clears, so "cleared" means the
                    // same raw value to the shader either way -- which is what its
                    // subject test reads.
                    ctx.cmd.ClearRenderTarget(true, false, Color.clear);

                    if (data.useLens)
                        ctx.cmd.SetViewProjectionMatrices(data.view, data.projection);

                    ctx.cmd.DrawRendererList(data.rendererList);

                    if (data.useLens)
                        ctx.cmd.SetViewProjectionMatrices(data.view, data.restoreProjection);
                });
            }

            // ── occlusion ───────────────────────────────────────────────────────────
            //
            // Half resolution. Nothing is lost -- the result is blurred, so it has no
            // detail finer than the blur to lose -- and it buys a quarter of the fill
            // plus a free extra smoothing step, because the last read back up to full
            // size is bilinear.
            int aoWidth = Mathf.Max(1, fullWidth / 2);
            int aoHeight = Mathf.Max(1, fullHeight / 2);

            var aoDesc = sourceDesc;
            aoDesc.width = aoWidth;
            aoDesc.height = aoHeight;

            // Two channels: occlusion and the linear depth it was computed at. Sixteen-bit
            // float rather than byte because occlusion is a gradient -- 256 steps of one
            // across a smooth surface is visible as banding once the blur has removed the
            // noise that was hiding it -- and because the second channel is a distance in
            // metres, which does not fit in a byte at all.
            //
            // The spill is NOT a third channel here. It was, and riding along in this
            // buffer meant it shared this buffer's resolution and this pass's fate; when
            // it failed it failed silently, with nothing to isolate. It has its own
            // buffers below.
            aoDesc.format = GraphicsFormat.R16G16_SFloat;
            aoDesc.depthBufferBits = 0;
            aoDesc.filterMode = FilterMode.Bilinear;
            aoDesc.clearBuffer = false;
            aoDesc.msaaSamples = MSAASamples.None;

            aoDesc.name = "NearFieldAO_A";
            var aoA = renderGraph.CreateTexture(aoDesc);

            aoDesc.name = "NearFieldAO_B";
            var aoB = renderGraph.CreateTexture(aoDesc);

            Vector2 tanHalf = TanHalf(camera, _fov);
            var screenSize = new Vector4(fullWidth, fullHeight, 1.0f / fullWidth, 1.0f / fullHeight);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field AO", out var passData))
            {
                passData.material = _materials.main;
                passData.subjectDepth = subjectDepth;
                passData.subjectNormals = subjectNormals;
                passData.shaderPass = ComputeShaderPass;
                passData.screenSize = screenSize;
                passData.tanHalf = tanHalf;
                passData.radius = _settings.radius;
                passData.intensity = _settings.intensity;
                passData.bias = _settings.bias;

                builder.UseTexture(subjectDepth, AccessFlags.Read);
                builder.UseTexture(subjectNormals, AccessFlags.Read);
                builder.SetRenderAttachment(aoA, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.material.SetVector(ScreenSizeId, data.screenSize);
                    data.material.SetVector(TanHalfId, data.tanHalf);
                    data.material.SetFloat(RadiusId, data.radius);
                    data.material.SetFloat(IntensityId, data.intensity);
                    data.material.SetFloat(BiasId, data.bias);
                    ctx.cmd.SetGlobalTexture(SubjectDepthTexId, data.subjectDepth);
                    ctx.cmd.SetGlobalTexture(SubjectNormalsTexId, data.subjectNormals);
                    Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                });
            }

            // Across, then down. Separable, so the cost is two rows of taps rather than a
            // square of them -- and this is the step that turns twenty-four samples per
            // pixel into something without a pattern in it.
            //
            // TWICE, because these subjects are close and the sampling disc is therefore
            // wide in pixels even at a radius of a centimetre. One five-tap pass reaches
            // about three texels; the gaps between samples are wider than that, and what
            // a blur cannot reach across it cannot smooth. A second iteration roughly
            // doubles the reach for the price of two more half-resolution passes, which
            // is the cheapest thing in this whole feature -- and it needs no new setting,
            // no new material, and no wider kernel to get wrong.
            var horizontal = new Vector2(1.0f / aoWidth, 0.0f);
            var vertical = new Vector2(0.0f, 1.0f / aoHeight);

            BlurStep(renderGraph, _materials.blurHorizontal, aoA, aoB, horizontal);
            BlurStep(renderGraph, _materials.blurVertical, aoB, aoA, vertical);
            BlurStep(renderGraph, _materials.blurHorizontal, aoA, aoB, horizontal);
            BlurStep(renderGraph, _materials.blurVertical, aoB, aoA, vertical);

            // ── the spill ───────────────────────────────────────────────────────────
            //
            // A blurred mask, in its own buffers, at its own resolution. Nothing about it
            // touches the occlusion above -- which is the point: it rode along in that
            // buffer as a third channel once, and when it produced nothing there was no
            // way to tell whether the fault was the channel, the format, the bilateral
            // filter that was never meant to carry it, or the value.
            //
            // Quarter resolution, because a soft wide ramp has no detail to lose and the
            // downsample is itself the first smoothing step: a bilinear read of a binary
            // field is already a gradient.
            TextureHandle spill = TextureHandle.nullHandle;

            if (wantsSpill)
            {
                int spillWidth = Mathf.Max(1, fullWidth / 4);
                int spillHeight = Mathf.Max(1, fullHeight / 4);

                var spillDesc = sourceDesc;
                spillDesc.width = spillWidth;
                spillDesc.height = spillHeight;
                spillDesc.format = GraphicsFormat.R16_SFloat;
                spillDesc.depthBufferBits = 0;
                spillDesc.filterMode = FilterMode.Bilinear;
                spillDesc.clearBuffer = false;
                spillDesc.msaaSamples = MSAASamples.None;

                spillDesc.name = "NearFieldSpill_A";
                var spillA = renderGraph.CreateTexture(spillDesc);

                spillDesc.name = "NearFieldSpill_B";
                var spillB = renderGraph.CreateTexture(spillDesc);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field Spill Mask", out var passData))
                {
                    passData.material = _materials.main;
                    passData.subjectDepth = subjectDepth;
                    passData.shaderPass = SpillMaskShaderPass;

                    builder.UseTexture(subjectDepth, AccessFlags.Read);
                    builder.SetRenderAttachment(spillA, 0, AccessFlags.Write);
                    builder.AllowGlobalStateModification(true);

                    builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.SetGlobalTexture(SubjectDepthTexId, data.subjectDepth);
                        Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                    });
                }

                // THE RADIUS BECOMES THE BLUR'S REACH, and this is the only arithmetic
                // the spill needs. The five-tap kernel's outermost tap sits at 3.23
                // texels, so one iteration reaches that far; two reach about 1.41 times
                // as far, the way variances add. Solving for the step that puts the total
                // reach at the authored radius is what makes the setting mean pixels
                // rather than "some blur".
                const float kernelReach = 3.2307692308f;
                const float iterationGain = 1.4142135624f;

                float texels = (_settings.spillRadius * 0.25f) / (kernelReach * iterationGain);
                texels = Mathf.Max(texels, 0.5f);

                var spillHorizontal = new Vector2(texels / spillWidth, 0.0f);
                var spillVertical = new Vector2(0.0f, texels / spillHeight);

                SpillBlurStep(renderGraph, _materials.spillBlurHorizontal, spillA, spillB, spillHorizontal);
                SpillBlurStep(renderGraph, _materials.spillBlurVertical, spillB, spillA, spillVertical);
                SpillBlurStep(renderGraph, _materials.spillBlurHorizontal, spillA, spillB, spillHorizontal);
                SpillBlurStep(renderGraph, _materials.spillBlurVertical, spillB, spillA, spillVertical);

                spill = spillA;
            }

            // ── over the colour ─────────────────────────────────────────────────────
            //
            // Into a temp, because a pass cannot read and write the same attachment.
            var tempDesc = sourceDesc;
            tempDesc.name = "NearFieldAO_Temp";
            var tempTexture = renderGraph.CreateTexture(tempDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field AO Composite", out var passData))
            {
                passData.material = _materials.main;
                passData.source = source;
                passData.subjectDepth = subjectDepth;
                passData.aoTexture = aoA;
                passData.shaderPass = CompositeShaderPass;
                passData.hasSpill = wantsSpill;
                passData.spillTexture = spill;

                // Zero when there is no spill buffer to read, which the shader takes as
                // its cue to leave the pixel alone rather than sample a texture nobody
                // bound.
                passData.spillOutward = wantsSpill ? _settings.spillOutward : 0.0f;
                passData.spillInward = wantsSpill ? _settings.spillInward : 0.0f;

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(subjectDepth, AccessFlags.Read);
                builder.UseTexture(aoA, AccessFlags.Read);

                if (wantsSpill)
                    builder.UseTexture(spill, AccessFlags.Read);

                builder.SetRenderAttachment(tempTexture, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.material.SetFloat(SpillOutwardId, data.spillOutward);
                    data.material.SetFloat(SpillInwardId, data.spillInward);
                    ctx.cmd.SetGlobalTexture(SubjectDepthTexId, data.subjectDepth);
                    ctx.cmd.SetGlobalTexture(AOTexId, data.aoTexture);

                    if (data.hasSpill)
                        ctx.cmd.SetGlobalTexture(SpillTexId, data.spillTexture);

                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field AO Copy Back", out var passData))
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

        // The mask's blur. Separate from the one below because it reads a different
        // texture, writes a different channel count and uses a different shader pass --
        // the only thing the two share is the name "blur".
        private void SpillBlurStep(RenderGraph renderGraph, Material material,
                                   TextureHandle from, TextureHandle to, Vector2 step)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field Spill Blur", out var passData))
            {
                passData.material = material;
                passData.spillTexture = from;
                passData.blurStep = step;
                passData.shaderPass = SpillBlurShaderPass;

                builder.UseTexture(from, AccessFlags.Read);
                builder.SetRenderAttachment(to, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.material.SetVector(BlurStepId, data.blurStep);
                    ctx.cmd.SetGlobalTexture(SpillTexId, data.spillTexture);
                    Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                });
            }
        }

        private void BlurStep(RenderGraph renderGraph, Material material,
                              TextureHandle from, TextureHandle to, Vector2 step)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Near Field AO Blur", out var passData))
            {
                passData.material = material;
                passData.aoTexture = from;
                passData.blurStep = step;
                passData.blurSharpness = _settings.blurDepthSharpness;
                passData.shaderPass = BlurShaderPass;

                builder.UseTexture(from, AccessFlags.Read);
                builder.SetRenderAttachment(to, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.material.SetVector(BlurStepId, data.blurStep);
                    data.material.SetFloat(BlurSharpnessId, data.blurSharpness);
                    ctx.cmd.SetGlobalTexture(AOTexId, data.aoTexture);
                    Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), data.material, data.shaderPass);
                });
            }
        }
    }
}

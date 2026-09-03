using System.Collections.Generic;
using UnityEngine;

// Cuts a limb off a skinned character at runtime and drops it on the floor.
//
// The character is one skinned mesh over one skeleton, so there is no "arm object"
// to unparent -- detaching a bone would only stretch the mesh, not break it. What
// happens instead is a split:
//
//   1. the mesh is baked in its current pose, giving real geometry to work with
//   2. the triangles weighted to the severed bone and everything under it are
//      copied out into a mesh of their own
//   3. that becomes a loose rigidbody and falls under the scene's gravity
//   4. the bone is collapsed to nothing, which pulls the vertices left behind on
//      the body down to a point and takes the same geometry out of the character
//
// Step 4 is what makes step 3 look like a cut rather than a duplicate, and it is
// also why this needs no art: collapsing a bone removes its geometry from every LOD
// at once, because they all share the skeleton.
//
// The seam is wherever the skin weights happen to fall, which is the honest cost of
// doing it this way -- there is no control over where the break lands, and it will
// not be a clean cut. Purpose-built severed-limb meshes are how a shipping game
// solves that; this solves it with no assets at all.
public class EnemyDismemberment : MonoBehaviour
{
    [Header("Model")]
    // Used to find the renderers that are actually on screen. Only LOD 0 is split --
    // the lower LODs never need a piece cut out of them, because collapsing the bone
    // has already emptied them.
    [SerializeField] private LODGroup lodGroup;

    [Header("Piece")]
    // Nothing is pushed anywhere -- a severed piece gets no linear impulse and falls
    // from where it stood, because throwing it along the shot looked like a prop
    // being swatted rather than a limb giving way.
    //
    // Spin is the exception, and it is what the linear push was reaching for. A piece
    // that drops perfectly level reads as a model being hidden; the same piece
    // turning as it goes reads as something that came loose. It applies to every cut
    // for that reason, a shot-off arm and a collapsing body alike -- neither of them
    // falls flat.
    [SerializeField] private float detachTorque = 2f;

    [SerializeField] private float pieceMass = 8f;


    // Seconds before a severed piece is cleaned up. Zero leaves it forever.
    [SerializeField] private float pieceLifetime = 30f;

    // Collapsed bones are held at zero every frame rather than set once, because the
    // Animator rewrites local scale from the clip on any rig whose animations touch
    // it -- and a bone that springs back to full size puts the limb back on.
    private readonly List<Transform> _collapsedBones = new();

    public bool IsDetached(Transform bone) => bone != null && _collapsedBones.Contains(bone);

    // Severs at the given bone, taking everything below it. The piece keeps the pose
    // it was cut in and falls from where it stood; no shot direction is needed,
    // because nothing is thrown.
    public void DetachPart(Transform bone)
    {
        if (bone == null || IsDetached(bone))
            return;

        HashSet<Transform> severed = CollectSubtree(bone);

        foreach (SkinnedMeshRenderer renderer in GetVisibleRenderers())
            ExtractPiece(renderer, severed, bone);

        _collapsedBones.Add(bone);
        bone.localScale = Vector3.zero;
    }

    private void LateUpdate()
    {
        // After the animation update, and after the hitboxes' own recoil, for the
        // same reason both of those are: anything written before the clip is
        // evaluated is simply overwritten by it.
        for (int i = 0; i < _collapsedBones.Count; i++)
        {
            if (_collapsedBones[i] != null)
                _collapsedBones[i].localScale = Vector3.zero;
        }
    }

    private IEnumerable<SkinnedMeshRenderer> GetVisibleRenderers()
    {
        if (lodGroup == null)
            lodGroup = GetComponentInChildren<LODGroup>();

        if (lodGroup == null)
        {
            foreach (SkinnedMeshRenderer renderer in GetComponentsInChildren<SkinnedMeshRenderer>())
                yield return renderer;

            yield break;
        }

        LOD[] levels = lodGroup.GetLODs();
        if (levels.Length == 0)
            yield break;

        foreach (Renderer renderer in levels[0].renderers)
        {
            if (renderer is SkinnedMeshRenderer skinned)
                yield return skinned;
        }
    }

    // Everything under the cut, minus anything already cut off below it.
    //
    // The exclusion is what makes a shatter work. Once a hand has come off, its bone
    // is collapsed but still owns its vertices, so a later cut at the forearm would
    // otherwise sweep those up and carry a squashed hand along inside the forearm
    // piece.
    private HashSet<Transform> CollectSubtree(Transform root)
    {
        var bones = new HashSet<Transform>();
        Gather(root);

        return bones;

        void Gather(Transform bone)
        {
            bones.Add(bone);

            foreach (Transform child in bone)
            {
                if (!_collapsedBones.Contains(child))
                    Gather(child);
            }
        }
    }

    // Takes the whole character apart at once and leaves nothing standing.
    //
    // Worked from the extremities inward, deepest bone first, because each cut only
    // claims what is still attached below it -- going the other way, the first cut
    // at the pelvis would claim the entire body and there would be one piece rather
    // than a dozen.
    public void Shatter()
    {
        var hitboxes = new List<EnemyHitbox>(GetComponentsInChildren<EnemyHitbox>(includeInactive: true));
        hitboxes.Sort((a, b) => DepthOf(b.transform).CompareTo(DepthOf(a.transform)));

        foreach (EnemyHitbox hitbox in hitboxes)
        {
            if (hitbox.Bone != null && !IsDetached(hitbox.Bone))
                DetachPart(hitbox.Bone);
        }

        // Told after the body is gone rather than before, so nothing is still trying
        // to walk on legs that are already lying on the floor.
        GetComponent<EnemyGolem>()?.Kill();
    }

    private static int DepthOf(Transform t)
    {
        int depth = 0;

        for (Transform p = t.parent; p != null; p = p.parent)
            depth++;

        return depth;
    }

    private void ExtractPiece(SkinnedMeshRenderer renderer, HashSet<Transform> severed,
        Transform bone)
    {
        Mesh source = renderer.sharedMesh;
        if (source == null)
            return;

        // Weights and indices can only be read from a mesh imported with Read/Write
        // enabled, and the import default is off. Without this the failure is a
        // wall of engine errors that never names the model, so it is named here.
        if (!source.isReadable)
        {
            Debug.LogError(
                $"EnemyDismemberment: '{source.name}' is not readable, so it cannot be cut up. " +
                "Tick Read/Write on the model's import settings.", renderer);
            return;
        }

        bool[] vertexIsSevered = MarkSeveredVertices(renderer, source, severed);
        if (vertexIsSevered == null)
            return;

        // Baked in the pose it is standing in right now, so the piece leaves from
        // where the limb visibly was rather than from the bind pose.
        //
        // useScale false, which despite the name is the one that comes out at the
        // size the character is drawn: the skinning matrices already carry the whole
        // hierarchy's scale, and useScale true divides the renderer's own scale back
        // out of the result. Passing true and then rescaling the piece, or passing
        // false and rescaling it, both go wrong in opposite directions -- the first
        // sheds pieces too small, the second too large.
        var baked = new Mesh();
        renderer.BakeMesh(baked, useScale: false);

        Mesh piece = BuildPieceMesh(baked, source, vertexIsSevered);
        DestroyImmediate(baked);

        if (piece == null)
            return;

        SpawnPiece(piece, renderer, bone);
    }

    // A vertex belongs to the piece when most of its weight is on severed bones.
    // A majority rather than any amount, because the vertices right at the joint are
    // shared between both sides -- taking every vertex with any severed influence
    // would tear a ring out of the surviving body as well.
    private static bool[] MarkSeveredVertices(SkinnedMeshRenderer renderer, Mesh source,
        HashSet<Transform> severed)
    {
        Transform[] bones = renderer.bones;
        var boneIsSevered = new bool[bones.Length];
        bool anySevered = false;

        for (int i = 0; i < bones.Length; i++)
        {
            boneIsSevered[i] = bones[i] != null && severed.Contains(bones[i]);
            anySevered |= boneIsSevered[i];
        }

        // This renderer has nothing on the severed limb -- an eye, a jaw.
        if (!anySevered)
            return null;

        BoneWeight[] weights = source.boneWeights;
        var result = new bool[weights.Length];
        bool anyVertex = false;

        for (int v = 0; v < weights.Length; v++)
        {
            BoneWeight w = weights[v];
            float share = 0f;

            if (boneIsSevered[w.boneIndex0]) share += w.weight0;
            if (boneIsSevered[w.boneIndex1]) share += w.weight1;
            if (boneIsSevered[w.boneIndex2]) share += w.weight2;
            if (boneIsSevered[w.boneIndex3]) share += w.weight3;

            result[v] = share > 0.5f;
            anyVertex |= result[v];
        }

        return anyVertex ? result : null;
    }

    // A triangle goes to whichever side owns most of its corners.
    //
    // Requiring all three instead is the obvious rule and loses geometry: a triangle
    // straddling a joint then belongs to neither side and is simply dropped, which
    // costs a ring of surface at every joint. One cut hides that inside the break,
    // but a full shatter makes thirteen of them and the character comes apart with
    // visible gaps where the seams were.
    //
    // Majority cannot lose anything, because a triangle has three corners and only
    // one side can hold two of them -- so every triangle is claimed exactly once
    // across all the cuts. The odd corner is copied along with it, so vertices that
    // never had a majority anywhere still arrive as part of somebody's triangle.
    private static Mesh BuildPieceMesh(Mesh baked, Mesh source, bool[] vertexIsSevered)
    {
        Vector3[] sourceVertices = baked.vertices;
        Vector3[] sourceNormals = baked.normals;
        Vector2[] sourceUvs = baked.uv;

        var remap = new Dictionary<int, int>();
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var submeshes = new List<List<int>>();

        for (int sub = 0; sub < source.subMeshCount; sub++)
        {
            int[] indices = source.GetTriangles(sub);
            var kept = new List<int>();

            for (int i = 0; i < indices.Length; i += 3)
            {
                int a = indices[i];
                int b = indices[i + 1];
                int c = indices[i + 2];

                int severedCorners = (vertexIsSevered[a] ? 1 : 0)
                                   + (vertexIsSevered[b] ? 1 : 0)
                                   + (vertexIsSevered[c] ? 1 : 0);

                if (severedCorners < 2)
                    continue;

                kept.Add(Remap(a));
                kept.Add(Remap(b));
                kept.Add(Remap(c));
            }

            submeshes.Add(kept);
        }

        if (vertices.Count == 0)
            return null;

        var piece = new Mesh { name = "SeveredPiece" };

        // Baked meshes routinely exceed what 16-bit indices can address, and the
        // failure is silent corruption rather than an error.
        piece.indexFormat = vertices.Count > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;

        piece.SetVertices(vertices);

        if (normals.Count == vertices.Count)
            piece.SetNormals(normals);

        if (uvs.Count == vertices.Count)
            piece.SetUVs(0, uvs);

        piece.subMeshCount = submeshes.Count;
        for (int sub = 0; sub < submeshes.Count; sub++)
            piece.SetTriangles(submeshes[sub], sub);

        piece.RecalculateBounds();

        if (normals.Count != vertices.Count)
            piece.RecalculateNormals();

        return piece;

        // Vertices are copied across the first time they are referenced, so the
        // piece carries only its own and its indices start from zero.
        int Remap(int index)
        {
            if (remap.TryGetValue(index, out int mapped))
                return mapped;

            mapped = vertices.Count;
            remap[index] = mapped;

            vertices.Add(sourceVertices[index]);

            if (sourceNormals.Length == sourceVertices.Length)
                normals.Add(sourceNormals[index]);

            if (sourceUvs.Length == sourceVertices.Length)
                uvs.Add(sourceUvs[index]);

            return mapped;
        }
    }

    private void SpawnPiece(Mesh piece, SkinnedMeshRenderer renderer, Transform bone)
    {
        var go = new GameObject($"Severed_{bone.name}");

        // The debris layer, from the one place the project defines it -- the same
        // definition every query excludes, so a piece is inert to shots, footing and
        // interaction without any of them being told about it individually.
        int layer = GameLayers.DebrisLayer;
        if (layer >= 0)
            go.layer = layer;

        // One, not the renderer's scale. The baked vertices already come out at the
        // size the character is actually drawn -- scaling the piece on top of that
        // applies the hierarchy's scale a second time, and an enlarged golem sheds
        // pieces enlarged again.
        go.transform.SetPositionAndRotation(renderer.transform.position, renderer.transform.rotation);
        go.transform.localScale = Vector3.one;

        go.AddComponent<MeshFilter>().sharedMesh = piece;
        go.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;

        // Convex, because a rigidbody cannot carry a concave one -- and because the
        // hull of a limb is close enough to the limb to bounce believably.
        MeshCollider collider = go.AddComponent<MeshCollider>();
        collider.sharedMesh = piece;
        collider.convex = true;

        Rigidbody body = go.AddComponent<Rigidbody>();
        body.mass = pieceMass;

        // A random axis rather than one derived from the shot, since there is no shot
        // in a collapse and a piece has no reason to prefer any particular tumble.
        body.AddTorque(Random.onUnitSphere * detachTorque, ForceMode.Impulse);

        if (pieceLifetime > 0f)
            Destroy(go, pieceLifetime);
    }
}

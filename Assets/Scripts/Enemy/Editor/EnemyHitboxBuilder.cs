using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

// Generates a per-bone hitbox set onto a character, sized from the skeleton itself.
//
// Seventeen colliders per enemy, each parented to the right bone, each aimed along
// it -- placing that by hand is an hour of dragging boxes and getting one of them
// subtly wrong, and it has to be redone the moment the rig changes. Sizing
// from the bones instead means the numbers come from the model rather than from
// eyeballing, and re-running fixes drift rather than duplicating it.
//
// The bone names are Unreal's standard skeleton, which is what this golem was
// exported with (pelvis / spine_01..03 / clavicle_l / upperarm_l / ...). Another
// character on the same skeleton works with no changes; one on a different skeleton
// needs its own name table, which is the one thing here worth copying rather than
// generalising -- naming conventions are not worth abstracting over until there are
// two of them.
public static class EnemyHitboxBuilder
{
    // Every collider ends up on this layer. Raycasts ignore the collision matrix, so
    // a layer with every box unticked is still shot-detectable while no longer
    // shoving the character controller around or catching on the world.
    private const string HitboxLayerName = "EnemyHitbox";

    private const string HitboxNamePrefix = "Hitbox_";

    // Recoil tuning, written onto every hitbox this builds.
    //
    // It lives here rather than only as the component's own defaults because a
    // serialized field's default applies once, when the component is first added --
    // changing the number in code afterwards leaves every hitbox already on a prefab
    // untouched. Putting it in the builder means re-running is how new values get
    // out, which is already how collider sizes work, so it is one rule rather than
    // two.
    //
    // Written by both menu items, and the difference between them: the full build
    // measures the skeleton and rewrites collider geometry as well, while Apply
    // Recoil Tuning writes only these. Once colliders have been nudged by hand the
    // retune is the one to use -- rebuilding would undo that work to change a spring.
    private const float RecoilSpring = 25f;
    private const float RecoilDamping = 7f;
    private const float MaxRecoilAngle = 60f;
    private const float ImpactPropagation = 0.3f;

    // Derived from whether losing the part ends the fight, rather than listed per
    // part, so the two can never drift: anything fatal is by definition the harder
    // thing to destroy, and marking a new part fatal gives it the tougher figure
    // without a second edit that could be forgotten.
    private const int FatalHitsToDetach = 6;
    private const int LimbHitsToDetach = 2;

    private readonly struct HitboxDef
    {
        public readonly EnemyBodyPart Part;

        // The bone the collider is parented to.
        public readonly string Bone;

        // The next bone down the chain. Only ever measured against -- it gives the
        // box its length and the direction to aim it, so nothing here needs to know
        // which axis the exporter ran down the bone.
        public readonly string Reference;

        // Half the box's thickness, as a fraction of that measured length, so a
        // bigger character gets proportionally bigger hitboxes without a second set
        // of numbers. Kept as a half-measure so the limb ratios carry over unchanged
        // from when these were capsules.
        public readonly float RadiusRatio;

        // A cube on the end of the bone rather than a box running its length. For the
        // parts that are a lump at the end of a chain and have no next bone to
        // measure toward -- the head, the hands.
        public readonly bool IsCompact;

        // Set where the reference bone is the WRONG way down the chain. Recoil turns
        // a part about its joint so the far end swings, which means it needs the
        // direction away from that joint -- true for every limb, whose reference is
        // the next bone out, but backwards for the head, whose only neighbour is the
        // neck underneath it.
        public readonly bool ReverseAxis;

        // Set on the parts that anchor the body rather than hang off it, so a round
        // landing on them does not move them. They still turn when a limb passes an
        // impact up.
        public readonly bool NoDirectRecoil;

        // Losing this part shatters the whole character. The head and the legs --
        // what a body cannot keep going without -- and the torso and pelvis, which
        // do not come off so much as give way.
        public readonly bool FatalDetach;

        // Direct hits this part survives. Fatal parts take far more than a limb: an
        // arm is a weak point worth aiming at precisely because the chest is not.
        public int HitsToDetach => FatalDetach ? FatalHitsToDetach : LimbHitsToDetach;

        public HitboxDef(EnemyBodyPart part, string bone, string reference, float radiusRatio,
            bool isCompact = false, bool reverseAxis = false, bool noDirectRecoil = false,
            bool fatalDetach = false)
        {
            Part = part;
            Bone = bone;
            Reference = reference;
            RadiusRatio = radiusRatio;
            IsCompact = isCompact;
            ReverseAxis = reverseAxis;
            NoDirectRecoil = noDirectRecoil;
            FatalDetach = fatalDetach;
        }
    }

    // Torso is one collider over the whole spine rather than one per spine bone:
    // three stacked capsules that all mean "chest" is three ways to say the same
    // thing, and the plan has nothing that treats spine_02 differently from
    // spine_03.
    private static readonly HitboxDef[] Definitions =
    {
        new HitboxDef(EnemyBodyPart.Head,  "head",     "neck_01", 0.9f, isCompact: true, reverseAxis: true, fatalDetach: true),
        new HitboxDef(EnemyBodyPart.Torso, "spine_01", "neck_01", 0.42f,
            noDirectRecoil: true, fatalDetach: true),
        new HitboxDef(EnemyBodyPart.Pelvis, "pelvis",  "spine_01", 0.7f,
            noDirectRecoil: true, fatalDetach: true),

        new HitboxDef(EnemyBodyPart.LeftShoulder, "clavicle_l", "upperarm_l", 0.65f),
        new HitboxDef(EnemyBodyPart.LeftUpperArm, "upperarm_l", "lowerarm_l", 0.3f),
        new HitboxDef(EnemyBodyPart.LeftLowerArm, "lowerarm_l", "hand_l",     0.28f),
        new HitboxDef(EnemyBodyPart.LeftHand,     "hand_l",     "index_01_l", 1.1f, isCompact: true),

        new HitboxDef(EnemyBodyPart.RightShoulder, "clavicle_r", "upperarm_r", 0.65f),
        new HitboxDef(EnemyBodyPart.RightUpperArm, "upperarm_r", "lowerarm_r", 0.3f),
        new HitboxDef(EnemyBodyPart.RightLowerArm, "lowerarm_r", "hand_r",     0.28f),
        new HitboxDef(EnemyBodyPart.RightHand,     "hand_r",     "index_01_r", 1.1f, isCompact: true),

        new HitboxDef(EnemyBodyPart.LeftThigh, "thigh_l", "calf_l", 0.28f, fatalDetach: true),
        new HitboxDef(EnemyBodyPart.LeftCalf,  "calf_l",  "foot_l", 0.26f, fatalDetach: true),
        new HitboxDef(EnemyBodyPart.LeftFoot,  "foot_l",  "ball_l", 0.45f),

        new HitboxDef(EnemyBodyPart.RightThigh, "thigh_r", "calf_r", 0.28f, fatalDetach: true),
        new HitboxDef(EnemyBodyPart.RightCalf,  "calf_r",  "foot_r", 0.26f, fatalDetach: true),
        new HitboxDef(EnemyBodyPart.RightFoot,  "foot_r",  "ball_r", 0.45f),
    };

    [MenuItem("Tools/Enemy/Build Hitboxes (Unreal Skeleton)")]
    private static void Build()
    {
        GameObject root = Selection.activeGameObject;
        if (root == null)
            return;

        Dictionary<string, Transform> bones = MapBones(root.transform);
        int layer = LayerMask.NameToLayer(HitboxLayerName);

        var report = new StringBuilder();
        int built = 0;

        foreach (HitboxDef def in Definitions)
        {
            if (!bones.TryGetValue(def.Bone, out Transform bone)
                || !bones.TryGetValue(def.Reference, out Transform reference))
            {
                report.AppendLine($"  skipped {def.Part}: no bone '{def.Bone}' or '{def.Reference}'");
                continue;
            }

            ApplyHitbox(def, bone, reference, layer);
            built++;
        }

        string missingLayer = layer < 0
            ? $"\nLayer '{HitboxLayerName}' does not exist -- colliders were left on their bones' layer. " +
              "Add it under Project Settings > Tags and Layers, untick its whole row in " +
              "Physics > Layer Collision Matrix, then run this again."
            : string.Empty;

        Debug.Log($"Built {built}/{Definitions.Length} hitboxes on '{root.name}'.\n{report}{missingLayer}", root);
    }

    [MenuItem("Tools/Enemy/Build Hitboxes (Unreal Skeleton)", isValidateFunction: true)]
    private static bool ValidateBuild() => Selection.activeGameObject != null;

    // Retunes without rebuilding.
    //
    // The full build measures the skeleton and rewrites every collider's size and
    // position from it, which is right the first time and wrong every time after --
    // by then those numbers have been nudged by hand and the measured ones are the
    // worse of the two. Tuning the spring should not cost that work.
    //
    // So this touches only what is genuinely authored in one place: the recoil
    // numbers, and which parts refuse a direct hit. Geometry, and the bone axis
    // derived from it, are left exactly as they are.
    [MenuItem("Tools/Enemy/Apply Recoil Tuning")]
    private static void ApplyRecoilTuning()
    {
        GameObject root = Selection.activeGameObject;
        if (root == null)
            return;

        EnemyHitbox[] hitboxes = root.GetComponentsInChildren<EnemyHitbox>(includeInactive: true);

        foreach (EnemyHitbox hitbox in hitboxes)
        {
            if (TryGetDefinition(hitbox.BodyPart, out HitboxDef def))
                WriteRecoilSettings(hitbox, def);
        }

        Debug.Log($"Retuned {hitboxes.Length} hitboxes on '{root.name}'. " +
                  "Collider sizes and positions untouched.", root);
    }

    [MenuItem("Tools/Enemy/Apply Recoil Tuning", isValidateFunction: true)]
    private static bool ValidateApplyRecoilTuning() => Selection.activeGameObject != null;

    // Recovered by part rather than passed in, so the retune path reaches the same
    // definition the build path used without having to carry it.
    private static bool TryGetDefinition(EnemyBodyPart part, out HitboxDef result)
    {
        foreach (HitboxDef def in Definitions)
        {
            if (def.Part == part)
            {
                result = def;
                return true;
            }
        }

        result = default;
        return false;
    }

    private static void WriteRecoilSettings(EnemyHitbox hitbox, HitboxDef def)
    {
        var serialized = new SerializedObject(hitbox);

        serialized.FindProperty("recoilSpring").floatValue = RecoilSpring;
        serialized.FindProperty("recoilDamping").floatValue = RecoilDamping;
        serialized.FindProperty("maxRecoilAngle").floatValue = MaxRecoilAngle;
        serialized.FindProperty("impactPropagation").floatValue = ImpactPropagation;
        serialized.FindProperty("takesDirectRecoil").boolValue = !def.NoDirectRecoil;

        // Every part can be destroyed, including the ones that will not be moved by
        // a hit -- being unshakeable is not the same as being indestructible, and a
        // golem that cannot be killed by shooting its chest is a golem with two
        // weak points and no body.
        serialized.FindProperty("canDetach").boolValue = true;
        serialized.FindProperty("detachIsFatal").boolValue = def.FatalDetach;
        serialized.FindProperty("hitsToDetach").intValue = def.HitsToDetach;

        serialized.ApplyModifiedProperties();
    }

    private const string ShowHitboxesPref = "ProjectArarat.ShowEnemyHitboxes";

    // Persisted so the setting survives a domain reload, which a plain static bool
    // does not -- it would silently switch itself off every time a script compiles,
    // which is often exactly while hitboxes are being looked at.
    [InitializeOnLoadMethod]
    private static void RestoreShowHitboxes()
    {
        EnemyHitbox.DrawAlways = EditorPrefs.GetBool(ShowHitboxesPref, false);
    }

    [MenuItem("Tools/Enemy/Show Hitboxes")]
    private static void ToggleShowHitboxes()
    {
        EnemyHitbox.DrawAlways = !EnemyHitbox.DrawAlways;
        EditorPrefs.SetBool(ShowHitboxesPref, EnemyHitbox.DrawAlways);

        SceneView.RepaintAll();
    }

    [MenuItem("Tools/Enemy/Show Hitboxes", isValidateFunction: true)]
    private static bool ValidateShowHitboxes()
    {
        Menu.SetChecked("Tools/Enemy/Show Hitboxes", EnemyHitbox.DrawAlways);
        return true;
    }

    // Reused rather than recreated when one already exists, so running this after a
    // rig change corrects the sizes in place instead of leaving a second set of
    // colliders behind the first.
    private static void ApplyHitbox(HitboxDef def, Transform bone, Transform reference, int layer)
    {
        string objectName = HitboxNamePrefix + def.Part;
        Transform existing = bone.Find(objectName);

        GameObject hitbox;
        if (existing != null)
        {
            hitbox = existing.gameObject;
        }
        else
        {
            hitbox = new GameObject(objectName);
            Undo.RegisterCreatedObjectUndo(hitbox, "Build Enemy Hitboxes");
            Undo.SetTransformParent(hitbox.transform, bone, "Build Enemy Hitboxes");
        }

        // Measured in the bone's own space, which is what makes the axis question go
        // away: whichever local axis the exporter ran the bone along is simply the
        // one this vector is longest on.
        Vector3 toReference = bone.InverseTransformPoint(reference.position);
        float length = toReference.magnitude;

        if (length < 0.0001f)
            length = 0.1f;

        Vector3 boneAxis = toReference / length;
        if (def.ReverseAxis)
            boneAxis = -boneAxis;

        Undo.RecordObject(hitbox.transform, "Build Enemy Hitboxes");
        hitbox.transform.localPosition = Vector3.zero;
        hitbox.transform.localScale = Vector3.one;

        // A BoxCollider's faces are locked to its own local axes, so the box is aimed
        // by turning the object it lives on rather than by any setting on the
        // collider. With local up pointing down the bone, the box's own Y is the
        // bone's length and its X and Z are the thickness -- which is why every size
        // below reads the same way regardless of which axis the exporter used.
        hitbox.transform.localRotation = Quaternion.FromToRotation(Vector3.up, boneAxis);

        if (layer >= 0)
            hitbox.layer = layer;

        ApplyBox(hitbox, length, length * def.RadiusRatio * 2f, def.IsCompact);

        EnemyHitbox marker = hitbox.GetComponent<EnemyHitbox>();
        if (marker == null)
            marker = Undo.AddComponent<EnemyHitbox>(hitbox);

        var serialized = new SerializedObject(marker);
        serialized.FindProperty("bodyPart").enumValueIndex = (int)def.Part;
        serialized.FindProperty("boneAxis").vector3Value = boneAxis;
        serialized.ApplyModifiedProperties();

        WriteRecoilSettings(marker, def);
    }

    // Both shapes are boxes; isCompact only decides whether the box runs the length
    // of a bone or sits as a cube on the end of one.
    //
    // Either way it is pushed entirely past the joint rather than centred on it. A
    // limb's flesh is between its joint and the next one, never behind it, and the
    // head and hands sit beyond their last joint for the same reason -- a box
    // centred on the wrist would put half of itself inside the forearm.
    private static void ApplyBox(GameObject hitbox, float length, float thickness, bool isCompact)
    {
        RemoveMismatchedCollider<BoxCollider>(hitbox);

        BoxCollider collider = hitbox.GetComponent<BoxCollider>();
        if (collider == null)
            collider = Undo.AddComponent<BoxCollider>(hitbox);

        Undo.RecordObject(collider, "Build Enemy Hitboxes");

        float alongBone = isCompact ? thickness : length;

        collider.size = new Vector3(thickness, alongBone, thickness);
        collider.center = new Vector3(0f, alongBone * 0.5f, 0f);
    }

    // A part that changed shape between runs would otherwise end up with both, and
    // two overlapping colliders means one bullet reported twice.
    private static void RemoveMismatchedCollider<TKeep>(GameObject hitbox) where TKeep : Collider
    {
        foreach (Collider collider in hitbox.GetComponents<Collider>())
        {
            if (collider is not TKeep)
                Undo.DestroyObjectImmediate(collider);
        }
    }

    // First bone wins on a duplicate name. The golem's LOD meshes repeat names like
    // "Body 1", but never bone names, so the ones this cares about are unique.
    private static Dictionary<string, Transform> MapBones(Transform root)
    {
        var bones = new Dictionary<string, Transform>();

        foreach (Transform t in root.GetComponentsInChildren<Transform>(includeInactive: true))
            bones.TryAdd(t.name, t);

        return bones;
    }
}

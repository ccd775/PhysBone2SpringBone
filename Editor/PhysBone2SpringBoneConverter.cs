using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniHumanoid;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace ccd775.AvatarPhysBoneConverter
{
    public enum PhysBone2SpringBoneIssueSeverity
    {
        Info,
        Warning,
        Error,
    }

    [Serializable]
    public sealed class PhysBone2SpringBoneIssue
    {
        public PhysBone2SpringBoneIssueSeverity Severity;
        public string ObjectPath;
        public string Message;

        public PhysBone2SpringBoneIssue(PhysBone2SpringBoneIssueSeverity severity, string objectPath, string message)
        {
            Severity = severity;
            ObjectPath = objectPath;
            Message = message;
        }

        public override string ToString()
        {
            return $"[{Severity}] {ObjectPath}: {Message}";
        }
    }

    [Serializable]
    public sealed class PhysBone2SpringBoneAnalysis
    {
        public int PhysBoneCount;
        public int SourceColliderCount;
        public int SpringCount;
        public int SimulatedSegmentCount;
        public int JointCount;
        public int ExplicitColliderReferenceCount;
        public int CapsuleColliderCount;
        public int PlaneColliderCount;
        public int SphereColliderCount;
        public readonly List<PhysBone2SpringBoneIssue> Issues = new List<PhysBone2SpringBoneIssue>();

        public int ErrorCount => Issues.Count(x => x.Severity == PhysBone2SpringBoneIssueSeverity.Error);
        public int WarningCount => Issues.Count(x => x.Severity == PhysBone2SpringBoneIssueSeverity.Warning);
        public bool CanConvert => PhysBoneCount > 0 && ErrorCount == 0;
    }

    [Serializable]
    public sealed class PhysBone2SpringBoneConversionOptions
    {
        public bool DeleteSourceComponents;
        public bool CreatePersistentVrmObject = true;
        public string VrmObjectFolder = "Assets/PhysBone2SpringBoneGenerated";
        public string Author = "Unknown";
    }

    public sealed class PhysBone2SpringBoneConversionResult
    {
        public PhysBone2SpringBoneAnalysis Analysis;
        public Vrm10Instance Instance;
        public PhysBone2SpringBoneManifest Manifest;
        public string VrmObjectAssetPath;
    }

    public static class PhysBone2SpringBoneConverter
    {
        public const string ConverterVersion = "2.3.0";
        private const float ReferenceDeltaTime = 1.0f / 60.0f;
        private const float Epsilon = 0.000001f;
        private const float FullyImmobileThreshold = 0.999f;
        private const float MaxForceRatio = 50.0f;
        private const string SpringNamePrefix = "[PhysBone2SpringBone:";

        private sealed class EdgePlan
        {
            public VRCPhysBoneBase.Bone Bone;
            public int BoneIndex;
            public int EndBoneIndex = -1;
            public Vector3 SyntheticLocalPosition;
            public bool IsSynthetic;
        }

        private sealed class SpringPlan
        {
            public VRCPhysBone Source;
            public readonly List<EdgePlan> Edges = new List<EdgePlan>();
        }

        private sealed class ConversionPlan
        {
            public Animator Root;
            public VRCPhysBone[] PhysBones;
            public VRCPhysBoneColliderBase[] Colliders;
            public readonly List<SpringPlan> Springs = new List<SpringPlan>();
            public readonly PhysBone2SpringBoneAnalysis Analysis = new PhysBone2SpringBoneAnalysis();
        }

        private struct JointSettings
        {
            public float Stiffness;
            public float GravityPower;
            public Vector3 GravityDirection;
            public float Drag;
            public float Radius;
            public UniGLTF.SpringBoneJobs.AnglelimitTypes AngleLimitType;
            public Quaternion LimitSpaceOffset;
            public float LimitPitch;
            public float LimitYaw;
        }

        public static PhysBone2SpringBoneAnalysis Analyze(Animator root)
        {
            return BuildPlan(root).Analysis;
        }

        public static PhysBone2SpringBoneConversionResult Convert(Animator root, PhysBone2SpringBoneConversionOptions options)
        {
            if (Application.isPlaying)
            {
                throw new InvalidOperationException("PhysBone conversion is only supported in Edit Mode.");
            }
            if (options == null)
            {
                options = new PhysBone2SpringBoneConversionOptions();
            }

            ValidateRoot(root);
            ValidateEditableRoot(root);
            var previousManifest = root.GetComponent<PhysBone2SpringBoneManifest>();
            if (previousManifest != null && previousManifest.SourceComponentsDeleted)
            {
                throw new InvalidOperationException("The previous conversion deleted its source components, so it cannot be rebuilt safely.");
            }

            var reusableVrmObject = previousManifest != null
                ? previousManifest.GeneratedVrmObject as VRM10Object
                : null;

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Convert PhysBone to VRM 1.0 SpringBone");

            string newlyCreatedAssetPath = null;
            try
            {
                if (previousManifest != null)
                {
                    RemoveGeneratedInternal(previousManifest, false);
                }

                var plan = BuildPlan(root);
                if (!plan.Analysis.CanConvert)
                {
                    throw new InvalidOperationException(BuildErrorMessage(plan.Analysis));
                }

                var humanoidWasPresent = root.TryGetComponent<Humanoid>(out var humanoid);
                if (!humanoidWasPresent)
                {
                    humanoid = Undo.AddComponent<Humanoid>(root.gameObject);
                    Undo.RecordObject(humanoid, "Assign humanoid bones");
                    if (!humanoid.AssignBonesFromAnimator())
                    {
                        throw new InvalidOperationException("The Animator does not have a valid humanoid Avatar.");
                    }
                }
                var humanoidErrors = humanoid.Validate().Where(x => x.IsError).Select(x => x.Message).ToArray();
                if (humanoidErrors.Length > 0)
                {
                    throw new InvalidOperationException("UniHumanoid mapping is incomplete: " + string.Join(", ", humanoidErrors));
                }

                var instanceWasPresent = root.TryGetComponent<Vrm10Instance>(out var instance);
                if (!instanceWasPresent)
                {
                    instance = Undo.AddComponent<Vrm10Instance>(root.gameObject);
                }
                if (instance.SpringBone == null)
                {
                    Undo.RecordObject(instance, "Initialize VRM SpringBone data");
                    instance.SpringBone = new Vrm10InstanceSpringBone();
                }

                var vrmObject = instance.Vrm;
                var vrmObjectOwnedByConverter = false;
                if (vrmObject == null && reusableVrmObject != null)
                {
                    vrmObject = reusableVrmObject;
                    vrmObjectOwnedByConverter = true;
                }
                if (vrmObject == null)
                {
                    vrmObject = CreateVrmObject(root, options, out newlyCreatedAssetPath);
                    vrmObjectOwnedByConverter = true;
                }
                Undo.RecordObject(instance, "Assign VRM 1.0 object");
                instance.Vrm = vrmObject;

                var manifest = Undo.AddComponent<PhysBone2SpringBoneManifest>(root.gameObject);
                manifest.ConverterVersion = ConverterVersion;
                manifest.ConversionId = Guid.NewGuid().ToString("N").Substring(0, 12);
                manifest.Vrm10Instance = instance;
                manifest.GeneratedVrmObject = vrmObjectOwnedByConverter ? vrmObject : null;
                manifest.GeneratedVrm10Instance = !instanceWasPresent;
                manifest.GeneratedHumanoid = !humanoidWasPresent;

                foreach (var source in plan.PhysBones.Cast<Behaviour>().Concat(plan.Colliders.Cast<Behaviour>()))
                {
                    manifest.SourceComponents.Add(source);
                    manifest.SourceEnabledStates.Add(source.enabled);
                    manifest.SourceEnabledWasPrefabOverride.Add(IsEnabledPrefabOverride(source));
                }

                Undo.RecordObject(instance, "Add converted SpringBone data");
                var colliderGroups = ConvertColliders(plan, instance, manifest);
                ConvertSprings(plan, instance, manifest, colliderGroups);

                if (options.DeleteSourceComponents)
                {
                    manifest.SourceComponentsDeleted = true;
                    var allSourceComponents = root.GetComponentsInChildren<VRCPhysBone>(true)
                        .Cast<Component>()
                        .Concat(root.GetComponentsInChildren<VRCPhysBoneColliderBase>(true))
                        .ToArray();
                    foreach (var source in allSourceComponents)
                    {
                        if (source != null)
                        {
                            Undo.DestroyObjectImmediate(source);
                        }
                    }
                    manifest.SourceComponents.Clear();
                    manifest.SourceEnabledStates.Clear();
                    manifest.SourceEnabledWasPrefabOverride.Clear();
                }
                else
                {
                    for (var i = 0; i < manifest.SourceComponents.Count; ++i)
                    {
                        var source = manifest.SourceComponents[i];
                        if (source == null)
                        {
                            continue;
                        }
                        Undo.RecordObject(source, "Disable source PhysBone component");
                        source.enabled = false;
                        RecordPrefabOverride(source);
                    }
                }

                EditorUtility.SetDirty(instance);
                EditorUtility.SetDirty(manifest);
                EditorUtility.SetDirty(humanoid);
                RecordPrefabOverride(instance);
                RecordPrefabOverride(humanoid);
                RecordPrefabOverride(manifest);
                if (AssetDatabase.Contains(vrmObject))
                {
                    AssetDatabase.SaveAssetIfDirty(vrmObject);
                }
                Undo.CollapseUndoOperations(undoGroup);

                return new PhysBone2SpringBoneConversionResult
                {
                    Analysis = plan.Analysis,
                    Instance = instance,
                    Manifest = manifest,
                    VrmObjectAssetPath = AssetDatabase.GetAssetPath(vrmObject),
                };
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                if (!string.IsNullOrEmpty(newlyCreatedAssetPath))
                {
                    AssetDatabase.DeleteAsset(newlyCreatedAssetPath);
                }
                throw;
            }
        }

        public static bool RemoveGenerated(Animator root)
        {
            if (Application.isPlaying)
            {
                throw new InvalidOperationException("Generated conversion data can only be removed in Edit Mode.");
            }
            if (root == null)
            {
                return false;
            }
            ValidateRoot(root);
            ValidateEditableRoot(root);
            var manifest = root.GetComponent<PhysBone2SpringBoneManifest>();
            if (manifest == null)
            {
                return false;
            }
            if (manifest.SourceComponentsDeleted)
            {
                throw new InvalidOperationException("The converted source components were deleted, so generated SpringBone data cannot be removed without leaving the avatar with no replacement physics.");
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Remove generated VRM SpringBone data");
            RemoveGeneratedInternal(manifest, true);
            Undo.CollapseUndoOperations(undoGroup);
            return true;
        }

        private static ConversionPlan BuildPlan(Animator root)
        {
            ValidateRoot(root);
            var manifest = root.GetComponent<PhysBone2SpringBoneManifest>();
            var allPhysBones = root.GetComponentsInChildren<VRCPhysBone>(true);
            var allColliders = root.GetComponentsInChildren<VRCPhysBoneColliderBase>(true);
            var plan = new ConversionPlan
            {
                Root = root,
                PhysBones = allPhysBones.Where(x => IsSourceActive(x, manifest)).ToArray(),
                Colliders = allColliders.Where(x => IsSourceActive(x, manifest)).ToArray(),
            };
            var analysis = plan.Analysis;
            analysis.PhysBoneCount = plan.PhysBones.Length;
            analysis.SourceColliderCount = plan.Colliders.Length;

            var skippedPhysBones = allPhysBones.Length - plan.PhysBones.Length;
            var skippedColliders = allColliders.Length - plan.Colliders.Length;
            if (skippedPhysBones > 0 || skippedColliders > 0)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Info, root.name,
                    $"Skipped {skippedPhysBones} inactive/disabled PhysBone component(s) and {skippedColliders} inactive/disabled collider(s) to match the avatar state exported by UniVRM.");
            }

            if (plan.PhysBones.Length == 0)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, root.name, "No VRC PhysBone components were found.");
                return plan;
            }

            if (root.avatar == null || !root.avatar.isValid || !root.avatar.isHuman)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, root.name, "Animator requires a valid humanoid Avatar for VRM 1.0.");
            }

            if (manifest != null)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Info, root.name, "A previous generated conversion exists and will be cleaned before rebuilding.");
            }

            var colliderSet = new HashSet<VRCPhysBoneColliderBase>(plan.Colliders);
            var jointOwners = new Dictionary<Transform, VRCPhysBone>();
            foreach (var physBone in plan.PhysBones)
            {
                BuildPhysBonePlan(plan, physBone, jointOwners, colliderSet);
            }

            foreach (var collider in plan.Colliders)
            {
                AnalyzeCollider(plan, collider);
            }

            analysis.SpringCount = plan.Springs.Count;
            analysis.SimulatedSegmentCount = plan.Springs.Sum(x => x.Edges.Count);
            analysis.JointCount = plan.Springs.Sum(x => x.Edges.Count + 1);
            analysis.ExplicitColliderReferenceCount = plan.PhysBones.Sum(x => x.colliders == null ? 0 : x.colliders.Count(y => y != null));
            return plan;
        }

        private static void BuildPhysBonePlan(
            ConversionPlan plan,
            VRCPhysBone physBone,
            Dictionary<Transform, VRCPhysBone> jointOwners,
            HashSet<VRCPhysBoneColliderBase> colliderSet)
        {
            var analysis = plan.Analysis;
            var path = GetComponentPath(plan.Root.transform, physBone);
            try
            {
                physBone.InitTransforms(true);
            }
            catch (Exception ex)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, path, "PhysBone topology initialization failed: " + ex.Message);
                return;
            }

            if (physBone.bones == null || physBone.bones.Count == 0)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, path, "The initialized PhysBone has no controlled transforms.");
                return;
            }

            var outgoing = new Dictionary<int, EdgePlan>();
            var incoming = new HashSet<int>();
            for (var i = 0; i < physBone.bones.Count; ++i)
            {
                var bone = physBone.bones[i];
                if (bone.transform == null || !bone.transform.IsChildOf(plan.Root.transform) && bone.transform != plan.Root.transform)
                {
                    AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, path, "A controlled transform is outside the selected avatar hierarchy.");
                    continue;
                }

                var edge = new EdgePlan { Bone = bone, BoneIndex = i };
                if (bone.childCount == 1 || bone.childCount > 1 && physBone.multiChildType == VRCPhysBoneBase.MultiChildType.First)
                {
                    if (bone.childIndex < 0 || bone.childIndex >= physBone.bones.Count)
                    {
                        AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, path, $"Invalid child index on {bone.transform.name}.");
                        continue;
                    }
                    edge.EndBoneIndex = bone.childIndex;
                    incoming.Add(edge.EndBoneIndex);
                }
                else if (bone.childCount > 1 && physBone.multiChildType == VRCPhysBoneBase.MultiChildType.Average)
                {
                    if (bone.averageChildPos == Vector3.zero)
                    {
                        continue;
                    }
                    edge.IsSynthetic = true;
                    edge.SyntheticLocalPosition = bone.averageChildPos;
                }
                else if (bone.childCount == 0 && physBone.endpointPosition != Vector3.zero)
                {
                    edge.IsSynthetic = true;
                    edge.SyntheticLocalPosition = physBone.endpointPosition;
                }
                else
                {
                    continue;
                }
                outgoing.Add(i, edge);
            }

            var visited = new HashSet<int>();
            foreach (var pair in outgoing.OrderBy(x => x.Key))
            {
                if (incoming.Contains(pair.Key))
                {
                    continue;
                }
                var spring = new SpringPlan { Source = physBone };
                var current = pair.Value;
                while (current != null && visited.Add(current.BoneIndex))
                {
                    spring.Edges.Add(current);
                    if (current.IsSynthetic || !outgoing.TryGetValue(current.EndBoneIndex, out current))
                    {
                        current = null;
                    }
                }
                if (spring.Edges.Count > 0)
                {
                    plan.Springs.Add(spring);
                }
            }

            foreach (var pair in outgoing)
            {
                if (!visited.Contains(pair.Key))
                {
                    AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, path, "The initialized topology contains an unsupported cycle or disconnected simulated edge.");
                }
            }

            var usedTransforms = new HashSet<Transform>();
            foreach (var edge in outgoing.Values)
            {
                usedTransforms.Add(edge.Bone.transform);
                if (!edge.IsSynthetic)
                {
                    usedTransforms.Add(physBone.bones[edge.EndBoneIndex].transform);
                }
            }
            foreach (var transform in usedTransforms)
            {
                if (jointOwners.TryGetValue(transform, out var owner) && owner != physBone)
                {
                    AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, path,
                        $"Transform '{GetRelativePath(plan.Root.transform, transform)}' is controlled by both '{owner.name}' and '{physBone.name}'. VRM 1.0 forbids a joint from belonging to multiple springs.");
                }
                else
                {
                    jointOwners[transform] = physBone;
                }

                var existingJoint = transform.GetComponent<VRM10SpringBoneJoint>();
                var existingManifest = plan.Root.GetComponent<PhysBone2SpringBoneManifest>();
                if (existingJoint != null &&
                    (existingManifest == null || !existingManifest.GeneratedComponents.Contains(existingJoint)))
                {
                    AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, path,
                        $"Transform '{GetRelativePath(plan.Root.transform, transform)}' already has a VRM10 joint that is not owned by this converter.");
                }
            }

            if (outgoing.Count == 0)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Warning, path, "No simulated segments can be represented; check endpoint and multi-child settings.");
            }
            AnalyzeUnsupportedPhysBoneFeatures(plan, physBone, path, outgoing.Values);

            if (physBone.colliders != null)
            {
                foreach (var collider in physBone.colliders)
                {
                    if (collider == null)
                    {
                        continue;
                    }
                    if (!colliderSet.Contains(collider))
                    {
                        AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, path,
                            $"Explicit collider '{collider.name}' is outside the selected avatar hierarchy or inactive/disabled. UniVRM would omit it, so the collision link cannot be exported safely.");
                    }
                }
            }
        }

        private static void AnalyzeUnsupportedPhysBoneFeatures(
            ConversionPlan plan,
            VRCPhysBone physBone,
            string path,
            IEnumerable<EdgePlan> edges)
        {
            var analysis = plan.Analysis;
            if (physBone.limitType != VRCPhysBoneBase.LimitType.None)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Info, path,
                    $"{physBone.limitType} angular limits will be preserved with the experimental VRMC_springBone_limit extension; runtimes that ignore this extension will simulate without limits.");
            }
            if (physBone.allowCollision != VRCPhysBoneBase.AdvancedBool.False)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Warning, path,
                    "Player hands and global/world collision permissions cannot be represented by VRM; explicit collider references are preserved.");
            }
            if (physBone.allowGrabbing != VRCPhysBoneBase.AdvancedBool.False ||
                physBone.allowPosing != VRCPhysBoneBase.AdvancedBool.False)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Warning, path,
                    "PhysBone grabbing, posing, snap-to-hand and permission filters have no VRM SpringBone equivalent.");
            }
            if (Mathf.Abs(physBone.maxStretch) > Epsilon || Mathf.Abs(physBone.maxSquish) > Epsilon)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Warning, path,
                    "Stretch and squish cannot be represented because VRM SpringBone keeps each segment at a fixed length.");
            }
            if (physBone.isAnimated)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Warning, path,
                    "Animated PhysBone rest poses are sampled at conversion time; VRM SpringBone does not update its rest pose from animation each frame.");
            }
            if (Mathf.Abs(physBone.gravityFalloff) > Epsilon)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Warning, path,
                    "Gravity Falloff is matched around the rest pose, but its angle-dependent response cannot be represented exactly.");
            }
            if (!string.IsNullOrEmpty(physBone.parameter))
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Warning, path,
                    "PhysBone animation parameters (_IsGrabbed/_Angle/_Stretch/_Squish) are not generated by VRM SpringBone.");
            }

            var edgeList = edges.ToList();
            if (edgeList.Count > 0)
            {
                var immobileValues = edgeList.Select(x => physBone.CalcImmobile(physBone.CalcBoneRatio(x.Bone.boneChainIndex))).ToArray();
                var minImmobile = immobileValues.Min();
                var maxImmobile = immobileValues.Max();
                if (maxImmobile > Epsilon && (minImmobile < 0.999f || maxImmobile - minImmobile > 0.001f))
                {
                    AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Warning, path,
                        "VRM Center cannot represent partial/per-joint Immobile. World-space inertia is preserved instead of quantizing partial values to fully follow the avatar.");
                }
            }

            if (physBone.bones.Any(x => x.childCount > 1))
            {
                if (physBone.multiChildType == VRCPhysBoneBase.MultiChildType.Average)
                {
                    AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Warning, path,
                        "Average multi-child endpoints are preserved with generated tail transforms, but VRM cannot reproduce VRC's branched solver exactly.");
                }
                else
                {
                    AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Warning, path,
                        "VRM SpringBone is linear, so this branched PhysBone is split into independent descendant springs. Parent-driven movement across the split can differ by one solver frame.");
                }
            }
        }

        private static void AnalyzeCollider(ConversionPlan plan, VRCPhysBoneColliderBase collider)
        {
            var analysis = plan.Analysis;
            var path = GetComponentPath(plan.Root.transform, collider);
            var rootTransform = collider.GetRootTransform();
            if (rootTransform == null || !rootTransform.IsChildOf(plan.Root.transform) && rootTransform != plan.Root.transform)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, path, "Collider rootTransform is outside the selected avatar hierarchy.");
            }

            switch (collider.shapeType)
            {
                case VRCPhysBoneColliderBase.ShapeType.Sphere:
                    analysis.SphereColliderCount++;
                    break;
                case VRCPhysBoneColliderBase.ShapeType.Capsule:
                    if (collider.height <= collider.radius * 2.0f)
                    {
                        analysis.SphereColliderCount++;
                        AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Info, path,
                            "The VRC capsule is degenerate (height <= diameter) and will be converted as a sphere, matching the VRC solver.");
                    }
                    else
                    {
                        analysis.CapsuleColliderCount++;
                    }
                    break;
                case VRCPhysBoneColliderBase.ShapeType.Plane:
                    analysis.PlaneColliderCount++;
                    if (collider.insideBounds)
                    {
                        AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Info, path,
                            "Inside Bounds has no effect on VRC plane colliders; the plane normal is preserved.");
                    }
                    break;
                default:
                    AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, path, "Unsupported VRC collider shape.");
                    break;
            }

            if (!collider.bonesAsSpheres && collider.shapeType != VRCPhysBoneColliderBase.ShapeType.Plane)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Warning, path,
                    "VRC can collide against the whole bone segment; standard VRM SpringBone collides at each simulated tail sphere only.");
            }
            if (collider.insideBounds && collider.shapeType != VRCPhysBoneColliderBase.ShapeType.Plane)
            {
                var scale = collider.GetRootTransform().lossyScale;
                if (Mathf.Abs(scale.x) <= Epsilon || Mathf.Abs(scale.y) <= Epsilon || Mathf.Abs(scale.z) <= Epsilon)
                {
                    AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Error, path,
                        "Inside collider root has a zero scale axis, so its world-space containment volume cannot be represented safely.");
                }
            }
            if (collider.globalCollisionFlags != DynamicsUsageFlags.Nothing)
            {
                AddIssue(analysis, PhysBone2SpringBoneIssueSeverity.Warning, path,
                    "VRC global collider registration cannot be represented; only explicit PhysBone-to-collider links are exported.");
            }
        }

        private static Dictionary<VRCPhysBoneColliderBase, VRM10SpringBoneColliderGroup> ConvertColliders(
            ConversionPlan plan,
            Vrm10Instance instance,
            PhysBone2SpringBoneManifest manifest)
        {
            var result = new Dictionary<VRCPhysBoneColliderBase, VRM10SpringBoneColliderGroup>();
            foreach (var source in plan.Colliders)
            {
                var colliderRoot = source.GetRootTransform();
                var maxScale = MaxAbsComponent(colliderRoot.lossyScale);
                var targetTransform = colliderRoot;
                if (source.insideBounds && source.shapeType != VRCPhysBoneColliderBase.ShapeType.Plane &&
                    Mathf.Abs(maxScale - 1.0f) > Epsilon)
                {
                    var proxy = new GameObject($"__PB2SB_{SanitizeName(source.name)}_InsideCollider");
                    Undo.RegisterCreatedObjectUndo(proxy, "Create normalized VRM inside collider");
                    Undo.SetTransformParent(proxy.transform, colliderRoot, "Parent normalized VRM inside collider");
                    proxy.transform.localPosition = Vector3.zero;
                    proxy.transform.localRotation = Quaternion.identity;
                    proxy.transform.localScale = ReciprocalScale(colliderRoot.lossyScale);
                    manifest.GeneratedObjects.Add(proxy);
                    targetTransform = proxy.transform;
                }

                var targetCollider = Undo.AddComponent<VRM10SpringBoneCollider>(targetTransform.gameObject);
                manifest.GeneratedComponents.Add(targetCollider);

                var localAxis = source.rotation * Vector3.up;
                var worldAxis = (colliderRoot.rotation * localAxis).normalized;
                var worldCenter = colliderRoot.TransformPoint(source.position);
                var storedRadiusScale = targetTransform == colliderRoot ? 1.0f : maxScale;
                var isDegenerateCapsule = source.shapeType == VRCPhysBoneColliderBase.ShapeType.Capsule &&
                                          source.height <= source.radius * 2.0f;
                if (source.shapeType == VRCPhysBoneColliderBase.ShapeType.Sphere || isDegenerateCapsule)
                {
                    targetCollider.ColliderType = source.insideBounds
                        ? VRM10SpringBoneColliderTypes.SphereInside
                        : VRM10SpringBoneColliderTypes.Sphere;
                    targetCollider.Offset = targetTransform.InverseTransformPoint(worldCenter);
                    targetCollider.Radius = source.radius * storedRadiusScale;
                }
                else if (source.shapeType == VRCPhysBoneColliderBase.ShapeType.Capsule)
                {
                    targetCollider.ColliderType = source.insideBounds
                        ? VRM10SpringBoneColliderTypes.CapsuleInside
                        : VRM10SpringBoneColliderTypes.Capsule;
                    var worldHalfSegment = Mathf.Max(0.0f, source.height * 0.5f - source.radius) * maxScale;
                    targetCollider.Offset = targetTransform.InverseTransformPoint(worldCenter - worldAxis * worldHalfSegment);
                    targetCollider.Tail = targetTransform.InverseTransformPoint(worldCenter + worldAxis * worldHalfSegment);
                    targetCollider.Radius = source.radius * storedRadiusScale;
                }
                else
                {
                    targetCollider.ColliderType = VRM10SpringBoneColliderTypes.Plane;
                    targetCollider.Offset = source.position;
                    targetCollider.Normal = colliderRoot.worldToLocalMatrix.MultiplyVector(worldAxis).normalized;
                }
                EditorUtility.SetDirty(targetCollider);
                RecordPrefabOverride(targetCollider);

                var targetGroup = Undo.AddComponent<VRM10SpringBoneColliderGroup>(source.gameObject);
                manifest.GeneratedComponents.Add(targetGroup);
                targetGroup.Name = GetUniqueColliderGroupName(source, plan.Colliders);
                targetGroup.Colliders.Add(targetCollider);
                instance.SpringBone.ColliderGroups.Add(targetGroup);
                EditorUtility.SetDirty(targetGroup);
                RecordPrefabOverride(targetGroup);
                result.Add(source, targetGroup);
            }
            return result;
        }

        private static void ConvertSprings(
            ConversionPlan plan,
            Vrm10Instance instance,
            PhysBone2SpringBoneManifest manifest,
            IReadOnlyDictionary<VRCPhysBoneColliderBase, VRM10SpringBoneColliderGroup> colliderGroups)
        {
            var jointMap = new Dictionary<Transform, VRM10SpringBoneJoint>();
            var sourceSpringIndices = new Dictionary<VRCPhysBone, int>();
            foreach (var springPlan in plan.Springs)
            {
                if (!sourceSpringIndices.TryGetValue(springPlan.Source, out var springIndex))
                {
                    springIndex = 0;
                }
                sourceSpringIndices[springPlan.Source] = springIndex + 1;

                var spring = new Vrm10InstanceSpringBone.Spring(
                    $"{SpringNamePrefix}{manifest.ConversionId}] {springPlan.Source.name}/{springIndex + 1}");
                spring.Center = ChooseCenter(plan.Root.transform, springPlan);

                VRM10SpringBoneJoint previousJoint = null;
                foreach (var edge in springPlan.Edges)
                {
                    var head = edge.Bone.transform;
                    var headJoint = GetOrCreateJoint(head, jointMap, manifest);
                    var tailPosition = edge.IsSynthetic
                        ? head.TransformPoint(edge.SyntheticLocalPosition)
                        : springPlan.Source.bones[edge.EndBoneIndex].transform.position;
                    ApplyJointSettings(headJoint, CalculateJointSettings(springPlan.Source, edge, tailPosition));
                    if (spring.Joints.Count == 0 || spring.Joints[spring.Joints.Count - 1] != headJoint)
                    {
                        spring.Joints.Add(headJoint);
                    }
                    previousJoint = headJoint;
                }

                var lastEdge = springPlan.Edges[springPlan.Edges.Count - 1];
                Transform tail;
                if (lastEdge.IsSynthetic)
                {
                    var tailObject = new GameObject($"__PB2SB_{SanitizeName(springPlan.Source.name)}_{SanitizeName(lastEdge.Bone.transform.name)}_Tail");
                    Undo.RegisterCreatedObjectUndo(tailObject, "Create VRM SpringBone tail");
                    Undo.SetTransformParent(tailObject.transform, lastEdge.Bone.transform, "Parent VRM SpringBone tail");
                    tailObject.transform.localPosition = lastEdge.SyntheticLocalPosition;
                    tailObject.transform.localRotation = Quaternion.identity;
                    tailObject.transform.localScale = Vector3.one;
                    manifest.GeneratedObjects.Add(tailObject);
                    tail = tailObject.transform;
                }
                else
                {
                    tail = springPlan.Source.bones[lastEdge.EndBoneIndex].transform;
                }

                var tailJoint = GetOrCreateJoint(tail, jointMap, manifest);
                if (previousJoint != null)
                {
                    tailJoint.m_stiffnessForce = previousJoint.m_stiffnessForce;
                    tailJoint.m_gravityPower = previousJoint.m_gravityPower;
                    tailJoint.m_gravityDir = previousJoint.m_gravityDir;
                    tailJoint.m_dragForce = previousJoint.m_dragForce;
                    tailJoint.m_jointRadius = previousJoint.m_jointRadius;
                    tailJoint.m_anglelimitType = previousJoint.m_anglelimitType;
                    tailJoint.m_limitSpaceOffset = previousJoint.m_limitSpaceOffset;
                    tailJoint.m_pitch = previousJoint.m_pitch;
                    tailJoint.m_yaw = previousJoint.m_yaw;
                    EditorUtility.SetDirty(tailJoint);
                }
                if (spring.Joints[spring.Joints.Count - 1] != tailJoint)
                {
                    spring.Joints.Add(tailJoint);
                }

                if (springPlan.Source.colliders != null)
                {
                    foreach (var sourceCollider in springPlan.Source.colliders)
                    {
                        if (sourceCollider != null && colliderGroups.TryGetValue(sourceCollider, out var group) &&
                            !spring.ColliderGroups.Contains(group))
                        {
                            spring.ColliderGroups.Add(group);
                        }
                    }
                }
                instance.SpringBone.Springs.Add(spring);
            }
        }

        private static VRM10SpringBoneJoint GetOrCreateJoint(
            Transform transform,
            IDictionary<Transform, VRM10SpringBoneJoint> jointMap,
            PhysBone2SpringBoneManifest manifest)
        {
            if (jointMap.TryGetValue(transform, out var joint))
            {
                return joint;
            }
            var existing = transform.GetComponent<VRM10SpringBoneJoint>();
            if (existing != null)
            {
                throw new InvalidOperationException($"Transform '{transform.name}' already has a VRM10SpringBoneJoint.");
            }
            joint = Undo.AddComponent<VRM10SpringBoneJoint>(transform.gameObject);
            manifest.GeneratedComponents.Add(joint);
            jointMap.Add(transform, joint);
            RecordPrefabOverride(joint);
            return joint;
        }

        private static JointSettings CalculateJointSettings(VRCPhysBone source, EdgePlan edge, Vector3 tailWorldPosition)
        {
            var ratio = source.CalcBoneRatio(edge.Bone.boneChainIndex);
            var pull = source.CalcPull(ratio);
            var spring = source.CalcSpring(ratio);
            var sourceStiffness = source.CalcStiffness(ratio);
            var gravity = source.CalcGravity(ratio);
            var gravityFalloff = source.CalcGravityFalloff(ratio);
            var effectiveGravity = gravity * (1.0f - gravityFalloff);
            var length = Mathf.Max(Vector3.Distance(edge.Bone.transform.position, tailWorldPosition), Epsilon);

            float momentum;
            float restRatio;
            float gravityRatio;
            if (source.integrationType == VRCPhysBoneBase.IntegrationType.Simplified)
            {
                var retainedVelocity = Mathf.Clamp01(0.99f * spring);
                var restCorrection = pull * (1.0f - retainedVelocity);
                var denominator = Mathf.Max(1.0f - restCorrection, 0.02f);
                momentum = retainedVelocity / denominator;
                restRatio = restCorrection / denominator;
                gravityRatio = (1.0f - retainedVelocity) / denominator;
            }
            else if (source.version == VRCPhysBoneBase.Version.Version_1_0)
            {
                var denominator = Mathf.Max(1.0f - pull + sourceStiffness, 0.02f);
                momentum = spring * (1.0f - pull) / denominator;
                restRatio = pull / denominator;
                gravityRatio = 1.0f / denominator;
            }
            else
            {
                var currentCoefficient = (1.0f - pull) * (1.0f - sourceStiffness) + sourceStiffness;
                var targetCoefficient = pull * (1.0f - sourceStiffness);
                var velocityCoefficient = spring * (1.0f - pull) * (1.0f - sourceStiffness);
                var denominator = Mathf.Max(currentCoefficient, 0.02f);
                momentum = velocityCoefficient / denominator;
                restRatio = targetCoefficient / denominator;
                gravityRatio = 0.0f;
            }

            momentum = Mathf.Clamp(momentum, 0.0f, 0.995f);
            restRatio = Mathf.Clamp(restRatio, 0.0f, MaxForceRatio);
            var gravityVector = Vector3.zero;
            if (source.version == VRCPhysBoneBase.Version.Version_1_0)
            {
                gravityVector = (effectiveGravity >= 0.0f ? Vector3.down : Vector3.up) *
                                (Mathf.Abs(effectiveGravity) * length * gravityRatio);
            }
            else if (Mathf.Abs(effectiveGravity) > Epsilon && restRatio > Epsilon)
            {
                var restDirection = (tailWorldPosition - edge.Bone.transform.position).normalized;
                var gravityDirection = effectiveGravity >= 0.0f ? Vector3.down : Vector3.up;
                var targetDirection = Vector3.LerpUnclamped(
                    restDirection,
                    gravityDirection,
                    Mathf.Abs(effectiveGravity)).normalized;
                gravityVector = (targetDirection - restDirection) * (restRatio * length);
            }

            var transformRatioDenominator = source.maxBoneChainIndex +
                                            (source.endpointPosition != Vector3.zero ? 1 : 0);
            var radiusRatio = transformRatioDenominator > 0
                ? source.CalcTransformRatio(edge.Bone.boneChainIndex + 1)
                : 0.0f;
            var localRadius = source.CalcRadius(radiusRatio);
            var radiusScale = MaxAbsComponent(edge.Bone.transform.lossyScale);
            if (!edge.IsSynthetic)
            {
                var endLocalScale = source.bones[edge.EndBoneIndex].transform.localScale;
                var headScale = edge.Bone.transform.lossyScale;
                radiusScale = MaxAbsComponent(Vector3.Scale(headScale, endLocalScale));
            }

            var maxAngle = source.CalcMaxAngle(ratio);
            var limitRotation = source.CalcLimitRotation(ratio);
            var sourceBoneVector = edge.IsSynthetic
                ? edge.SyntheticLocalPosition
                : source.bones[edge.EndBoneIndex].transform.localPosition;
            var runtimeTailScale = edge.IsSynthetic
                ? edge.Bone.transform.lossyScale
                : source.bones[edge.EndBoneIndex].transform.lossyScale;
            var runtimeBoneVector = Vector3.Scale(sourceBoneVector, runtimeTailScale);
            return new JointSettings
            {
                Stiffness = restRatio * length / ReferenceDeltaTime,
                GravityPower = gravityVector.magnitude / ReferenceDeltaTime,
                GravityDirection = gravityVector.sqrMagnitude > Epsilon * Epsilon
                    ? gravityVector.normalized
                    : Vector3.down,
                Drag = 1.0f - momentum,
                Radius = Mathf.Max(0.0f, localRadius * radiusScale),
                AngleLimitType = ConvertAngleLimitType(source.limitType),
                LimitSpaceOffset = CalculateLimitSpaceOffset(sourceBoneVector, runtimeBoneVector, limitRotation),
                LimitPitch = Mathf.Clamp(maxAngle.x * Mathf.Deg2Rad, 0.0f, Mathf.PI),
                LimitYaw = Mathf.Clamp(maxAngle.y * Mathf.Deg2Rad, 0.0f, Mathf.PI * 0.5f),
            };
        }

        private static UniGLTF.SpringBoneJobs.AnglelimitTypes ConvertAngleLimitType(VRCPhysBoneBase.LimitType sourceType)
        {
            switch (sourceType)
            {
                case VRCPhysBoneBase.LimitType.Angle:
                    return UniGLTF.SpringBoneJobs.AnglelimitTypes.Cone;
                case VRCPhysBoneBase.LimitType.Hinge:
                    return UniGLTF.SpringBoneJobs.AnglelimitTypes.Hinge;
                case VRCPhysBoneBase.LimitType.Polar:
                    return UniGLTF.SpringBoneJobs.AnglelimitTypes.Spherical;
                default:
                    return UniGLTF.SpringBoneJobs.AnglelimitTypes.None;
            }
        }

        private static Quaternion CalculateLimitSpaceOffset(
            Vector3 sourceBoneVector,
            Vector3 runtimeBoneVector,
            Vector3 limitRotation)
        {
            var sourceAxis = sourceBoneVector.sqrMagnitude > Epsilon * Epsilon
                ? sourceBoneVector.normalized
                : Vector3.up;
            var runtimeAxis = runtimeBoneVector.sqrMagnitude > Epsilon * Epsilon
                ? runtimeBoneVector.normalized
                : sourceAxis;
            var vrcLimitSpace = Quaternion.FromToRotation(Vector3.up, sourceAxis) *
                                EulerXyz(limitRotation * Mathf.Deg2Rad);
            var springBoneAxisSpace = GetSpringBoneAxisRotation(runtimeAxis);
            return Quaternion.Normalize(Quaternion.Inverse(springBoneAxisSpace) * vrcLimitSpace);
        }

        private static Quaternion GetSpringBoneAxisRotation(Vector3 axis)
        {
            var dotPlusOne = axis.y + 1.0f;
            if (dotPlusOne < 0.00000001f)
            {
                return new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);
            }
            return Quaternion.Normalize(new Quaternion(axis.z, 0.0f, -axis.x, dotPlusOne));
        }

        private static Quaternion EulerXyz(Vector3 radians)
        {
            var half = radians * 0.5f;
            var sx = Mathf.Sin(half.x);
            var cx = Mathf.Cos(half.x);
            var sy = Mathf.Sin(half.y);
            var cy = Mathf.Cos(half.y);
            var sz = Mathf.Sin(half.z);
            var cz = Mathf.Cos(half.z);
            return Quaternion.Normalize(new Quaternion(
                sx * cy * cz - sy * sz * cx,
                sy * cx * cz + sx * sz * cy,
                sz * cx * cy - sx * sy * cz,
                cx * cy * cz + sy * sz * sx));
        }

        private static void ApplyJointSettings(VRM10SpringBoneJoint joint, JointSettings settings)
        {
            joint.m_stiffnessForce = settings.Stiffness;
            joint.m_gravityPower = settings.GravityPower;
            joint.m_gravityDir = settings.GravityDirection;
            joint.m_dragForce = settings.Drag;
            joint.m_jointRadius = settings.Radius;
            joint.m_anglelimitType = settings.AngleLimitType;
            joint.m_limitSpaceOffset = settings.LimitSpaceOffset;
            joint.m_pitch = settings.LimitPitch;
            joint.m_yaw = settings.LimitYaw;
            EditorUtility.SetDirty(joint);
            RecordPrefabOverride(joint);
        }

        private static Transform ChooseCenter(Transform avatarRoot, SpringPlan spring)
        {
            foreach (var edge in spring.Edges)
            {
                var ratio = spring.Source.CalcBoneRatio(edge.Bone.boneChainIndex);
                if (spring.Source.CalcImmobile(ratio) < FullyImmobileThreshold)
                {
                    return null;
                }
            }

            if (spring.Source.immobileType == VRCPhysBoneBase.ImmobileType.World)
            {
                return avatarRoot;
            }
            var parent = spring.Source.GetRootTransform().parent;
            return parent != null && (parent == avatarRoot || parent.IsChildOf(avatarRoot)) ? parent : avatarRoot;
        }

        private static VRM10Object CreateVrmObject(
            Animator root,
            PhysBone2SpringBoneConversionOptions options,
            out string createdAssetPath)
        {
            createdAssetPath = null;
            var vrmObject = ScriptableObject.CreateInstance<VRM10Object>();
            vrmObject.name = root.name + "_VRM10";
            vrmObject.Meta.Name = root.name;
            vrmObject.Meta.Authors = new List<string>
            {
                string.IsNullOrWhiteSpace(options.Author) ? "Unknown" : options.Author.Trim(),
            };
            vrmObject.Prefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(root.gameObject);

            if (!options.CreatePersistentVrmObject)
            {
                vrmObject.hideFlags = HideFlags.DontSave;
                return vrmObject;
            }

            var folder = NormalizeAssetFolder(options.VrmObjectFolder);
            EnsureAssetFolder(folder);
            var fileName = SanitizeFileName(root.name) + "_VRM10.asset";
            createdAssetPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + fileName);
            AssetDatabase.CreateAsset(vrmObject, createdAssetPath);
            AssetDatabase.SaveAssetIfDirty(vrmObject);
            return AssetDatabase.LoadAssetAtPath<VRM10Object>(createdAssetPath);
        }

        private static void RemoveGeneratedInternal(PhysBone2SpringBoneManifest manifest, bool destroyTransientVrmObject)
        {
            if (manifest == null)
            {
                return;
            }

            var instance = manifest.Vrm10Instance as Vrm10Instance;
            if (instance != null)
            {
                Undo.RecordObject(instance, "Remove generated SpringBone data");
                if (instance.SpringBone != null)
                {
                    var generatedJoints = new HashSet<VRM10SpringBoneJoint>(
                        manifest.GeneratedComponents.OfType<VRM10SpringBoneJoint>().Where(x => x != null));
                    instance.SpringBone.Springs.RemoveAll(x =>
                        x != null &&
                        (!string.IsNullOrEmpty(x.Name) && x.Name.StartsWith(SpringNamePrefix, StringComparison.Ordinal) ||
                         x.Joints != null && x.Joints.Any(generatedJoints.Contains)));
                    foreach (var component in manifest.GeneratedComponents)
                    {
                        if (component is VRM10SpringBoneColliderGroup group)
                        {
                            instance.SpringBone.ColliderGroups.Remove(group);
                        }
                    }
                }
                if (instance.Vrm == manifest.GeneratedVrmObject)
                {
                    instance.Vrm = null;
                }
                EditorUtility.SetDirty(instance);
            }

            if (!manifest.SourceComponentsDeleted)
            {
                var count = Mathf.Min(manifest.SourceComponents.Count, manifest.SourceEnabledStates.Count);
                for (var i = 0; i < count; ++i)
                {
                    var source = manifest.SourceComponents[i];
                    if (source == null)
                    {
                        continue;
                    }
                    Undo.RecordObject(source, "Restore source PhysBone component");
                    source.enabled = manifest.SourceEnabledStates[i];
                    RecordPrefabOverride(source);
                    var shouldRevertEnabledOverride = manifest.SourceEnabledWasPrefabOverride != null &&
                                                      i < manifest.SourceEnabledWasPrefabOverride.Count
                        ? !manifest.SourceEnabledWasPrefabOverride[i]
                        : EnabledMatchesPrefabSource(source);
                    if (shouldRevertEnabledOverride)
                    {
                        RevertEnabledPrefabOverride(source);
                    }
                }
            }

            for (var i = manifest.GeneratedComponents.Count - 1; i >= 0; --i)
            {
                var component = manifest.GeneratedComponents[i];
                if (component != null)
                {
                    Undo.DestroyObjectImmediate(component);
                }
            }
            for (var i = manifest.GeneratedObjects.Count - 1; i >= 0; --i)
            {
                var generatedObject = manifest.GeneratedObjects[i];
                if (generatedObject != null)
                {
                    Undo.DestroyObjectImmediate(generatedObject);
                }
            }

            if (manifest.GeneratedVrm10Instance && instance != null)
            {
                Undo.DestroyObjectImmediate(instance);
            }
            if (manifest.GeneratedHumanoid)
            {
                var humanoid = manifest.GetComponent<Humanoid>();
                if (humanoid != null)
                {
                    Undo.DestroyObjectImmediate(humanoid);
                }
            }

            var generatedVrmObject = manifest.GeneratedVrmObject;
            Undo.DestroyObjectImmediate(manifest);
            if (destroyTransientVrmObject && generatedVrmObject != null && !AssetDatabase.Contains(generatedVrmObject))
            {
                UnityEngine.Object.DestroyImmediate(generatedVrmObject);
            }
        }

        private static string BuildErrorMessage(PhysBone2SpringBoneAnalysis analysis)
        {
            var errors = analysis.Issues
                .Where(x => x.Severity == PhysBone2SpringBoneIssueSeverity.Error)
                .Take(8)
                .Select(x => x.ObjectPath + ": " + x.Message);
            return "Conversion analysis found blocking errors:\n" + string.Join("\n", errors);
        }

        private static string GetUniqueColliderGroupName(VRCPhysBoneColliderBase source, IEnumerable<VRCPhysBoneColliderBase> colliders)
        {
            var sameObject = colliders.Where(x => x != null && x.gameObject == source.gameObject).ToArray();
            if (sameObject.Length <= 1)
            {
                return source.name;
            }
            return source.name + " #" + (Array.IndexOf(sameObject, source) + 1);
        }

        private static bool IsSourceActive(Behaviour source, PhysBone2SpringBoneManifest manifest)
        {
            if (source == null || !source.gameObject.activeInHierarchy)
            {
                return false;
            }

            var enabled = source.enabled;
            if (!enabled && manifest != null &&
                manifest.SourceComponents != null && manifest.SourceEnabledStates != null)
            {
                var index = manifest.SourceComponents.IndexOf(source);
                if (index >= 0 && index < manifest.SourceEnabledStates.Count)
                {
                    enabled = manifest.SourceEnabledStates[index];
                }
            }
            if (!enabled)
            {
                return false;
            }

            if (source is VRCPhysBone physBone)
            {
                var boneRoot = physBone.GetRootTransform();
                return boneRoot != null && boneRoot.gameObject.activeInHierarchy;
            }
            if (source is VRCPhysBoneColliderBase collider)
            {
                var colliderRoot = collider.GetRootTransform();
                return colliderRoot != null && colliderRoot.gameObject.activeInHierarchy;
            }
            return true;
        }

        private static void ValidateRoot(Animator root)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root), "Select an avatar Animator first.");
            }
            if (EditorUtility.IsPersistent(root))
            {
                throw new InvalidOperationException("Select a scene instance, not a project asset.");
            }
        }

        private static void ValidateEditableRoot(Animator root)
        {
            var prefabStage = PrefabStageUtility.GetPrefabStage(root.gameObject);
            var scenePath = root.gameObject.scene.path;
            if (prefabStage != null ||
                EditorSceneManager.IsPreviewSceneObject(root.gameObject) ||
                !string.IsNullOrEmpty(scenePath) && scenePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Prefab Mode, loaded Prefab contents, and Preview Scenes are read-only for this converter. Instantiate the avatar in a normal scene first so the source Prefab cannot be overwritten.");
            }
        }

        private static void AddIssue(
            PhysBone2SpringBoneAnalysis analysis,
            PhysBone2SpringBoneIssueSeverity severity,
            string path,
            string message)
        {
            analysis.Issues.Add(new PhysBone2SpringBoneIssue(severity, path, message));
        }

        private static string GetComponentPath(Transform root, Component component)
        {
            return GetRelativePath(root, component.transform) + " (" + component.GetType().Name + ")";
        }

        private static string GetRelativePath(Transform root, Transform target)
        {
            if (target == root)
            {
                return root.name;
            }
            var names = new Stack<string>();
            var current = target;
            while (current != null && current != root)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return current == root ? root.name + "/" + string.Join("/", names) : target.name;
        }

        private static float MaxAbsComponent(Vector3 value)
        {
            return Mathf.Max(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        }

        private static Vector3 ReciprocalScale(Vector3 value)
        {
            return new Vector3(1.0f / value.x, 1.0f / value.y, 1.0f / value.z);
        }

        private static string SanitizeName(string value)
        {
            return string.IsNullOrEmpty(value) ? "Unnamed" : value.Replace('/', '_').Replace('\\', '_');
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (string.IsNullOrWhiteSpace(value) ? "Avatar" : value)
                .Select(x => invalid.Contains(x) ? '_' : x)
                .ToArray();
            return new string(chars);
        }

        private static string NormalizeAssetFolder(string folder)
        {
            folder = string.IsNullOrWhiteSpace(folder)
                ? "Assets/PhysBone2SpringBoneGenerated"
                : folder.Replace('\\', '/').TrimEnd('/');
            if (folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("VRM10Object output folder must be inside Assets.");
            }
            return folder;
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }
            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; ++i)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private static bool IsEnabledPrefabOverride(Behaviour source)
        {
            if (source == null || !PrefabUtility.IsPartOfPrefabInstance(source))
            {
                return false;
            }
            var property = new SerializedObject(source).FindProperty("m_Enabled");
            return property != null && property.prefabOverride;
        }

        private static bool EnabledMatchesPrefabSource(Behaviour source)
        {
            var prefabSource = source != null
                ? PrefabUtility.GetCorrespondingObjectFromSource(source) as Behaviour
                : null;
            return prefabSource != null && source.enabled == prefabSource.enabled;
        }

        private static void RevertEnabledPrefabOverride(Behaviour source)
        {
            if (source == null || !PrefabUtility.IsPartOfPrefabInstance(source))
            {
                return;
            }
            var property = new SerializedObject(source).FindProperty("m_Enabled");
            if (property != null && property.prefabOverride)
            {
                PrefabUtility.RevertPropertyOverride(property, InteractionMode.AutomatedAction);
            }
        }

        private static void RecordPrefabOverride(UnityEngine.Object target)
        {
            if (target != null && PrefabUtility.IsPartOfPrefabInstance(target))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            }
        }
    }
}

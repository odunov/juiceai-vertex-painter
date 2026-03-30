using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace JuiceAI.VertexPainter.Editor
{
    public enum VertexPaintOwnershipLevel
    {
        SceneInstance = 0,
        ContainerPrefab = 1,
        FoundationPrefab = 2
    }

    public readonly struct VertexPaintResolvedTarget
    {
        public VertexPaintResolvedTarget(
            VertexPaintOwnershipLevel level,
            string displayName,
            string categoryName,
            bool isAvailable,
            string reason,
            string scopeLabel,
            string scopeKey,
            string objectKey,
            string assetPath,
            string relativePath,
            bool editsLiveObject,
            GameObject resolvedObject,
            bool hasPaint,
            bool ownsPaint,
            bool suppressesInheritedPaint)
        {
            Level = level;
            DisplayName = displayName;
            CategoryName = categoryName;
            IsAvailable = isAvailable;
            Reason = reason;
            ScopeLabel = scopeLabel;
            ScopeKey = scopeKey;
            ObjectKey = objectKey;
            AssetPath = assetPath;
            RelativePath = relativePath;
            EditsLiveObject = editsLiveObject;
            ResolvedObject = resolvedObject;
            HasPaint = hasPaint;
            OwnsPaint = ownsPaint;
            SuppressesInheritedPaint = suppressesInheritedPaint;
        }

        public VertexPaintOwnershipLevel Level { get; }
        public string DisplayName { get; }
        public string CategoryName { get; }
        public bool IsAvailable { get; }
        public string Reason { get; }
        public string ScopeLabel { get; }
        public string ScopeKey { get; }
        public string ObjectKey { get; }
        public string AssetPath { get; }
        public string RelativePath { get; }
        public bool EditsLiveObject { get; }
        public GameObject ResolvedObject { get; }
        public bool HasPaint { get; }
        public bool OwnsPaint { get; }
        public bool SuppressesInheritedPaint { get; }
        public VertexPaintBinding Binding => ResolvedObject != null ? ResolvedObject.GetComponent<VertexPaintBinding>() : null;
        public VertexPaintAsset PaintAsset => Binding != null ? Binding.PaintAsset : null;
    }

    public static class VertexPaintAuthoringContextUtility
    {
        public static VertexPaintResolvedTarget Evaluate(GameObject target, VertexPaintOwnershipLevel level)
        {
            return Evaluate(target, level, null);
        }

        public static VertexPaintResolvedTarget Evaluate(GameObject target, VertexPaintOwnershipLevel level, GameObject authoringRoot)
        {
            return level switch
            {
                VertexPaintOwnershipLevel.SceneInstance => EvaluateSceneInstance(target),
                VertexPaintOwnershipLevel.ContainerPrefab => EvaluateContainerPrefab(target, authoringRoot),
                VertexPaintOwnershipLevel.FoundationPrefab => EvaluateFoundationPrefab(target),
                _ => Unavailable(level, "Unsupported vertex paint level.")
            };
        }

        public static VertexPaintResolvedTarget ResolveCurrentPaintTarget(GameObject target)
        {
            return ResolveCurrentPaintTarget(target, null);
        }

        public static VertexPaintResolvedTarget ResolveCurrentPaintTarget(GameObject target, GameObject authoringRoot)
        {
            VertexPaintResolvedTarget sceneTarget = Evaluate(target, VertexPaintOwnershipLevel.SceneInstance, authoringRoot);
            VertexPaintResolvedTarget containerTarget = Evaluate(target, VertexPaintOwnershipLevel.ContainerPrefab, authoringRoot);
            VertexPaintResolvedTarget foundationTarget = Evaluate(target, VertexPaintOwnershipLevel.FoundationPrefab, authoringRoot);
            return ResolveCurrentPaintTarget(target, sceneTarget, containerTarget, foundationTarget);
        }

        public static VertexPaintResolvedTarget ResolveCurrentPaintTarget(
            GameObject target,
            VertexPaintResolvedTarget sceneTarget,
            VertexPaintResolvedTarget containerTarget,
            VertexPaintResolvedTarget foundationTarget)
        {
            if (containerTarget.IsAvailable)
            {
                return containerTarget;
            }

            if (sceneTarget.IsAvailable)
            {
                return sceneTarget;
            }

            return Unavailable(
                VertexPaintOwnershipLevel.ContainerPrefab,
                foundationTarget.IsAvailable
                    ? foundationTarget.Reason
                    : "Select a mesh target first.");
        }

        public static VertexPaintResolvedTarget NormalizeSceneTarget(
            VertexPaintResolvedTarget sceneTarget,
            VertexPaintResolvedTarget containerTarget,
            VertexPaintResolvedTarget foundationTarget)
        {
            if (!sceneTarget.IsAvailable || sceneTarget.Level != VertexPaintOwnershipLevel.SceneInstance)
            {
                return sceneTarget;
            }

            VertexPaintBinding binding = sceneTarget.Binding;
            VertexPaintAsset inheritedPaintAsset = GetInheritedScenePaintAsset(containerTarget, foundationTarget);
            bool hasPaint = binding != null && binding.PaintAsset != null;
            bool ownsPaint = hasPaint && binding.PaintAsset != inheritedPaintAsset;
            bool suppressesInheritedPaint =
                binding != null &&
                binding.PaintAsset == null &&
                inheritedPaintAsset != null &&
                sceneTarget.SuppressesInheritedPaint;

            return new VertexPaintResolvedTarget(
                sceneTarget.Level,
                sceneTarget.DisplayName,
                sceneTarget.CategoryName,
                sceneTarget.IsAvailable,
                sceneTarget.Reason,
                sceneTarget.ScopeLabel,
                sceneTarget.ScopeKey,
                sceneTarget.ObjectKey,
                sceneTarget.AssetPath,
                sceneTarget.RelativePath,
                sceneTarget.EditsLiveObject,
                sceneTarget.ResolvedObject,
                hasPaint,
                ownsPaint,
                suppressesInheritedPaint);
        }

        public static VertexPaintOwnershipLevel? GetVisiblePaintOwner(
            VertexPaintResolvedTarget sceneTarget,
            VertexPaintResolvedTarget containerTarget,
            VertexPaintResolvedTarget foundationTarget)
        {
            if (sceneTarget.IsAvailable)
            {
                if (sceneTarget.OwnsPaint)
                {
                    return VertexPaintOwnershipLevel.SceneInstance;
                }

                if (sceneTarget.SuppressesInheritedPaint)
                {
                    return null;
                }
            }

            if (containerTarget.IsAvailable)
            {
                if (containerTarget.OwnsPaint)
                {
                    return VertexPaintOwnershipLevel.ContainerPrefab;
                }

                if (containerTarget.SuppressesInheritedPaint)
                {
                    return null;
                }
            }

            if (foundationTarget.IsAvailable && foundationTarget.HasPaint)
            {
                return VertexPaintOwnershipLevel.FoundationPrefab;
            }

            return null;
        }

        public static VertexPaintOwnershipLevel? GetEffectiveOwner(
            VertexPaintResolvedTarget sceneTarget,
            VertexPaintResolvedTarget containerTarget,
            VertexPaintResolvedTarget foundationTarget)
        {
            return GetVisiblePaintOwner(sceneTarget, containerTarget, foundationTarget);
        }

        public static bool IsSameTarget(VertexPaintResolvedTarget left, VertexPaintResolvedTarget right)
        {
            if (!left.IsAvailable || !right.IsAvailable)
            {
                return false;
            }

            return left.ScopeKey == right.ScopeKey &&
                   left.ObjectKey == right.ObjectKey &&
                   left.RelativePath == right.RelativePath;
        }

        public static string GetHierarchyRelativePath(Transform root, Transform target)
        {
            if (root == null || target == null)
            {
                return string.Empty;
            }

            if (root == target)
            {
                return string.Empty;
            }

            Stack<string> segments = new();
            Transform current = target;
            while (current != null && current != root)
            {
                segments.Push(BuildRelativePathSegment(current));
                current = current.parent;
            }

            if (current != root)
            {
                return string.Empty;
            }

            return segments.Count == 0 ? string.Empty : string.Join("/", segments);
        }

        private static VertexPaintResolvedTarget EvaluateSceneInstance(GameObject target)
        {
            if (target == null)
            {
                return Unavailable(VertexPaintOwnershipLevel.SceneInstance, "Select a mesh target first.");
            }

            if (IsPrefabStageObject(target) || EditorUtility.IsPersistent(target))
            {
                return Unavailable(
                    VertexPaintOwnershipLevel.SceneInstance,
                    "Scene Instance only exists on placed scene objects.");
            }

            string scenePath = target.scene.path;
            string scopeLabel = string.IsNullOrWhiteSpace(scenePath)
                ? string.IsNullOrWhiteSpace(target.scene.name) ? "UnsavedScene" : target.scene.name
                : Path.GetFileNameWithoutExtension(scenePath);

            string scopeKey = string.IsNullOrWhiteSpace(scenePath)
                ? $"UnsavedScene:{scopeLabel}"
                : scenePath;

            VertexPaintBinding binding = target.GetComponent<VertexPaintBinding>();
            bool hasPaint = binding != null && binding.PaintAsset != null;
            bool ownsPaint = hasPaint && IsSceneLocalOwner(target, binding);
            bool suppressesInheritedPaint = binding != null && binding.PaintAsset == null && IsSceneLocalOverride(target, binding);

            return new VertexPaintResolvedTarget(
                VertexPaintOwnershipLevel.SceneInstance,
                "Scene Instance",
                "Scenes",
                true,
                string.Empty,
                scopeLabel,
                scopeKey,
                GetObjectKey(target),
                string.Empty,
                string.Empty,
                true,
                target,
                hasPaint,
                ownsPaint,
                suppressesInheritedPaint);
        }

        private static VertexPaintResolvedTarget EvaluateContainerPrefab(GameObject target, GameObject authoringRoot)
        {
            if (target == null)
            {
                return Unavailable(VertexPaintOwnershipLevel.ContainerPrefab, "Select a mesh target first.");
            }

            PrefabStage stage = PrefabStageUtility.GetPrefabStage(target);
            if (stage != null && target.scene == stage.scene)
            {
                PrefabAssetType stageAssetType = PrefabUtility.GetPrefabAssetType(stage.prefabContentsRoot);
                if (stageAssetType == PrefabAssetType.Model)
                {
                    return Unavailable(
                        VertexPaintOwnershipLevel.ContainerPrefab,
                        "The open prefab is a model prefab, which is read-only.");
                }

                GameObject resolvedAuthoringRoot = ResolveStageContainerAuthoringRoot(target, authoringRoot, stage);
                if (resolvedAuthoringRoot == null)
                {
                    return Unavailable(
                        VertexPaintOwnershipLevel.ContainerPrefab,
                        "This mesh is not inside the currently open prefab root.");
                }

                return CreateResolvedTarget(
                    target,
                    VertexPaintOwnershipLevel.ContainerPrefab,
                    "Shared Prefab",
                    "ContainerPrefabs",
                    stage.assetPath,
                    GetHierarchyRelativePath(resolvedAuthoringRoot.transform, target.transform),
                    true,
                    target);
            }

            if (EditorUtility.IsPersistent(target))
            {
                return Unavailable(
                    VertexPaintOwnershipLevel.ContainerPrefab,
                    "Open the prefab in Prefab Mode or select a placed scene object to edit shared prefab paint.");
            }

            if (!TryResolveContainerInScene(target, authoringRoot, out GameObject assetObject, out string assetPath, out string relativePath))
            {
                return Unavailable(
                    VertexPaintOwnershipLevel.ContainerPrefab,
                    "This mesh is not part of an editable shared prefab.");
            }

            return CreateResolvedTarget(
                target,
                VertexPaintOwnershipLevel.ContainerPrefab,
                "Shared Prefab",
                "ContainerPrefabs",
                assetPath,
                relativePath,
                false,
                assetObject);
        }

        private static VertexPaintResolvedTarget EvaluateFoundationPrefab(GameObject target)
        {
            if (target == null)
            {
                return Unavailable(VertexPaintOwnershipLevel.FoundationPrefab, "Select a mesh target first.");
            }

            PrefabStage stage = PrefabStageUtility.GetPrefabStage(target);
            if (stage != null &&
                target.scene == stage.scene &&
                TryUseOpenPrefabStageAsFoundation(target, stage, out VertexPaintResolvedTarget stageTarget))
            {
                return stageTarget;
            }

            if (!TryResolveImmediateSource(target, out GameObject sourceObject, out string assetPath))
            {
                return Unavailable(
                    VertexPaintOwnershipLevel.FoundationPrefab,
                    "This mesh does not have an editable foundation prefab.");
            }

            if (PrefabUtility.IsPartOfModelPrefab(sourceObject))
            {
                return Unavailable(
                    VertexPaintOwnershipLevel.FoundationPrefab,
                    "This mesh comes from a model prefab, which is read-only.");
            }

            string relativePath = string.Empty;
            if (TryGetNearestInstanceRootForAssetPath(target, assetPath, out GameObject instanceRoot))
            {
                relativePath = GetHierarchyRelativePath(instanceRoot.transform, target.transform);
            }

            return CreateResolvedTarget(
                target,
                VertexPaintOwnershipLevel.FoundationPrefab,
                "Foundation Prefab",
                "FoundationPrefabs",
                assetPath,
                relativePath,
                false,
                sourceObject);
        }

        private static VertexPaintResolvedTarget CreateResolvedTarget(
            GameObject selectionTarget,
            VertexPaintOwnershipLevel level,
            string displayName,
            string categoryName,
            string assetPath,
            string relativePath,
            bool editsLiveObject,
            GameObject resolvedObject)
        {
            VertexPaintBinding binding = resolvedObject != null ? resolvedObject.GetComponent<VertexPaintBinding>() : null;
            bool hasPaint = binding != null && binding.PaintAsset != null;
            bool ownsPaint = false;
            bool suppressesInheritedPaint = false;

            if (resolvedObject != null)
            {
                if (level == VertexPaintOwnershipLevel.SceneInstance)
                {
                    ownsPaint = hasPaint && IsSceneLocalOwner(resolvedObject, binding);
                    suppressesInheritedPaint = binding != null && binding.PaintAsset == null && IsSceneLocalOverride(resolvedObject, binding);
                }
                else
                {
                    EvaluateAssetOwnerState(resolvedObject, binding, out ownsPaint, out suppressesInheritedPaint);
                }
            }

            string normalizedAssetPath = VertexPaintEditorUtility.NormalizeAssetPath(assetPath);
            string scopeLabel = Path.GetFileNameWithoutExtension(normalizedAssetPath);
            string scopeKey = normalizedAssetPath;
            string objectKey = resolvedObject != null
                ? GetObjectKey(resolvedObject)
                : $"{scopeKey}:{relativePath}";

            return new VertexPaintResolvedTarget(
                level,
                displayName,
                categoryName,
                true,
                string.Empty,
                scopeLabel,
                scopeKey,
                objectKey,
                normalizedAssetPath,
                relativePath,
                editsLiveObject,
                resolvedObject,
                hasPaint,
                ownsPaint,
                suppressesInheritedPaint);
        }

        private static VertexPaintResolvedTarget Unavailable(VertexPaintOwnershipLevel level, string reason)
        {
            string displayName = level switch
            {
                VertexPaintOwnershipLevel.SceneInstance => "Scene Instance",
                VertexPaintOwnershipLevel.ContainerPrefab => "Shared Prefab",
                VertexPaintOwnershipLevel.FoundationPrefab => "Source Prefab",
                _ => "Unavailable"
            };

            string categoryName = level switch
            {
                VertexPaintOwnershipLevel.ContainerPrefab => "ContainerPrefabs",
                VertexPaintOwnershipLevel.FoundationPrefab => "FoundationPrefabs",
                _ => "Scenes"
            };

            return new VertexPaintResolvedTarget(
                level,
                displayName,
                categoryName,
                false,
                reason,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                null,
                false,
                false,
                false);
        }

        private static bool TryUseOpenPrefabStageAsFoundation(
            GameObject target,
            PrefabStage stage,
            out VertexPaintResolvedTarget resolvedTarget)
        {
            resolvedTarget = default;
            if (stage == null || target == null || target.scene != stage.scene)
            {
                return false;
            }

            GameObject immediateSourceObject = PrefabUtility.GetCorrespondingObjectFromSource(target);
            if (immediateSourceObject != null)
            {
                return false;
            }

            PrefabAssetType stageAssetType = PrefabUtility.GetPrefabAssetType(stage.prefabContentsRoot);
            if (stageAssetType == PrefabAssetType.Model)
            {
                resolvedTarget = Unavailable(
                    VertexPaintOwnershipLevel.FoundationPrefab,
                    "This mesh comes from a model prefab, which is read-only.");
                return true;
            }

            resolvedTarget = CreateResolvedTarget(
                target,
                VertexPaintOwnershipLevel.FoundationPrefab,
                "Foundation Prefab",
                "FoundationPrefabs",
                stage.assetPath,
                GetHierarchyRelativePath(stage.prefabContentsRoot.transform, target.transform),
                true,
                target);
            return true;
        }

        private static bool TryResolveImmediateSource(GameObject target, out GameObject sourceObject, out string assetPath)
        {
            sourceObject = null;
            assetPath = string.Empty;
            if (target == null)
            {
                return false;
            }

            sourceObject = PrefabUtility.GetCorrespondingObjectFromSource(target);
            if (sourceObject == null)
            {
                return false;
            }

            assetPath = VertexPaintEditorUtility.NormalizeAssetPath(AssetDatabase.GetAssetPath(sourceObject));
            return !string.IsNullOrWhiteSpace(assetPath);
        }

        private static bool TryResolveContainerInScene(
            GameObject target,
            GameObject authoringRoot,
            out GameObject assetObject,
            out string assetPath,
            out string relativePath)
        {
            assetObject = null;
            assetPath = string.Empty;
            relativePath = string.Empty;
            if (target == null)
            {
                return false;
            }

            string foundationAssetPath = string.Empty;
            if (TryResolveImmediateSource(target, out _, out string immediateSourcePath))
            {
                foundationAssetPath = immediateSourcePath;
            }

            GameObject containerRoot = ResolveSceneContainerAuthoringRoot(target, authoringRoot);
            if (containerRoot == null || !IsAncestorOrSelf(containerRoot.transform, target.transform))
            {
                return false;
            }

            assetPath = VertexPaintEditorUtility.NormalizeAssetPath(
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(containerRoot));

            if (string.IsNullOrWhiteSpace(assetPath) ||
                PrefabUtility.GetPrefabAssetType(containerRoot) == PrefabAssetType.Model)
            {
                return false;
            }

            relativePath = GetHierarchyRelativePath(containerRoot.transform, target.transform);
            return TryGetPersistentAssetObject(target, assetPath, out assetObject);
        }

        private static bool TryGetNearestInstanceRootForAssetPath(GameObject target, string assetPath, out GameObject instanceRoot)
        {
            instanceRoot = null;
            string normalizedAssetPath = VertexPaintEditorUtility.NormalizeAssetPath(assetPath);
            Transform current = target != null ? target.transform : null;
            while (current != null)
            {
                GameObject candidate = current.gameObject;
                if (!PrefabUtility.IsAnyPrefabInstanceRoot(candidate))
                {
                    current = current.parent;
                    continue;
                }

                string candidatePath = VertexPaintEditorUtility.NormalizeAssetPath(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(candidate));
                if (candidatePath == normalizedAssetPath)
                {
                    instanceRoot = candidate;
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static bool TryGetPersistentAssetObject(GameObject target, string assetPath, out GameObject assetObject)
        {
            assetObject = null;
            if (target == null || string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            Object corresponding = PrefabUtility.GetCorrespondingObjectFromSourceAtPath(target, assetPath);
            assetObject = corresponding as GameObject;
            return assetObject != null;
        }

        private static bool IsSceneLocalOwner(GameObject target, VertexPaintBinding binding)
        {
            return binding != null && binding.PaintAsset != null && IsSceneLocalOverride(target, binding);
        }

        private static string BuildRelativePathSegment(Transform transform)
        {
            return $"{transform.name}[{GetSameNameSiblingOrdinal(transform)}]";
        }

        private static int GetSameNameSiblingOrdinal(Transform transform)
        {
            if (transform == null || transform.parent == null)
            {
                return 0;
            }

            int ordinal = 0;
            for (int i = 0; i < transform.parent.childCount; i++)
            {
                Transform sibling = transform.parent.GetChild(i);
                if (sibling == transform)
                {
                    return ordinal;
                }

                if (sibling.name == transform.name)
                {
                    ordinal++;
                }
            }

            return ordinal;
        }

        private static VertexPaintAsset GetInheritedScenePaintAsset(
            VertexPaintResolvedTarget containerTarget,
            VertexPaintResolvedTarget foundationTarget)
        {
            if (containerTarget.IsAvailable && containerTarget.OwnsPaint)
            {
                return containerTarget.PaintAsset;
            }

            if (foundationTarget.IsAvailable && foundationTarget.HasPaint)
            {
                return foundationTarget.PaintAsset;
            }

            return null;
        }

        private static bool IsSceneLocalOverride(GameObject target, VertexPaintBinding binding)
        {
            if (target == null || binding == null)
            {
                return false;
            }

            if (!PrefabUtility.IsPartOfPrefabInstance(target))
            {
                return true;
            }

            if (PrefabUtility.IsAddedComponentOverride(binding))
            {
                return true;
            }

            SerializedObject serializedObject = new(binding);
            SerializedProperty paintAssetProperty = serializedObject.FindProperty("paintAsset");
            return paintAssetProperty != null && paintAssetProperty.prefabOverride;
        }

        private static void EvaluateAssetOwnerState(
            GameObject targetObject,
            VertexPaintBinding binding,
            out bool ownsPaint,
            out bool suppressesInheritedPaint)
        {
            ownsPaint = false;
            suppressesInheritedPaint = false;

            if (targetObject == null || binding == null)
            {
                return;
            }

            GameObject parentSourceObject = PrefabUtility.GetCorrespondingObjectFromSource(targetObject);
            VertexPaintBinding parentSourceBinding = parentSourceObject != null ? parentSourceObject.GetComponent<VertexPaintBinding>() : null;

            if (parentSourceBinding == null)
            {
                ownsPaint = binding.PaintAsset != null;
                return;
            }

            if (binding.PaintAsset != parentSourceBinding.PaintAsset)
            {
                ownsPaint = binding.PaintAsset != null;
                suppressesInheritedPaint = binding.PaintAsset == null;
            }
        }

        private static bool IsPrefabStageObject(GameObject target)
        {
            PrefabStage stage = target != null ? PrefabStageUtility.GetPrefabStage(target) : null;
            return stage != null && target.scene == stage.scene;
        }

        private static GameObject ResolveSceneContainerAuthoringRoot(GameObject target, GameObject authoringRoot)
        {
            if (target == null || EditorUtility.IsPersistent(target))
            {
                return null;
            }

            if (authoringRoot != null &&
                authoringRoot.scene == target.scene &&
                IsAncestorOrSelf(authoringRoot.transform, target.transform))
            {
                GameObject normalizedAuthoringRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(authoringRoot);
                if (normalizedAuthoringRoot != null &&
                    IsAncestorOrSelf(normalizedAuthoringRoot.transform, target.transform))
                {
                    return normalizedAuthoringRoot;
                }
            }

            GameObject outermostInstanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(target);
            return outermostInstanceRoot != null && IsAncestorOrSelf(outermostInstanceRoot.transform, target.transform)
                ? outermostInstanceRoot
                : null;
        }

        private static GameObject ResolveStageContainerAuthoringRoot(
            GameObject target,
            GameObject authoringRoot,
            PrefabStage stage)
        {
            if (target == null || stage == null || stage.prefabContentsRoot == null || target.scene != stage.scene)
            {
                return null;
            }

            if (authoringRoot != null &&
                authoringRoot.scene == stage.scene &&
                IsAncestorOrSelf(stage.prefabContentsRoot.transform, authoringRoot.transform) &&
                IsAncestorOrSelf(authoringRoot.transform, target.transform))
            {
                return stage.prefabContentsRoot;
            }

            return IsAncestorOrSelf(stage.prefabContentsRoot.transform, target.transform)
                ? stage.prefabContentsRoot
                : null;
        }

        private static bool IsAncestorOrSelf(Transform ancestor, Transform target)
        {
            if (ancestor == null || target == null)
            {
                return false;
            }

            Transform current = target;
            while (current != null)
            {
                if (current == ancestor)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static string GetObjectKey(GameObject target)
        {
            if (target == null)
            {
                return string.Empty;
            }

            GlobalObjectId globalId = GlobalObjectId.GetGlobalObjectIdSlow(target);
            string serialized = globalId.ToString();
            if (!string.IsNullOrWhiteSpace(serialized) && serialized != "GlobalObjectId_V1-0-0-0-0-0")
            {
                return serialized;
            }

            if (EditorUtility.IsPersistent(target))
            {
                string assetPath = VertexPaintEditorUtility.NormalizeAssetPath(AssetDatabase.GetAssetPath(target));
                return $"{assetPath}:{VertexPaintEditorUtility.GetHierarchyPath(target.transform)}";
            }

            return $"{target.scene.name}:{VertexPaintEditorUtility.GetHierarchyPath(target.transform)}:{target.GetInstanceID()}";
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace JuiceAI.VertexPainter.Editor
{
    public static class VertexPaintEditorUtility
    {
        private const string PaintAssetPropertyName = "paintAsset";

        private static readonly VertexAttributeDescriptor[] StreamDescriptor =
        {
            new(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4)
        };

        public static string NormalizeAssetPath(string assetPath)
        {
            return string.IsNullOrWhiteSpace(assetPath)
                ? string.Empty
                : assetPath.Replace('\\', '/').TrimEnd('/');
        }

        public static bool TryGetModuleRootPath(out string moduleRootPath)
        {
            moduleRootPath = string.Empty;
            string[] guids = AssetDatabase.FindAssets("JuiceAI.VertexPainter.Runtime t:AssemblyDefinitionAsset");
            if (guids == null || guids.Length == 0)
            {
                return false;
            }

            string runtimeAsmdefPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            string runtimeFolder = Path.GetDirectoryName(runtimeAsmdefPath);
            string root = string.IsNullOrWhiteSpace(runtimeFolder) ? string.Empty : Path.GetDirectoryName(runtimeFolder);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            moduleRootPath = NormalizeAssetPath(root);
            return true;
        }

        public static string GetHierarchyPath(Transform target)
        {
            if (target == null)
            {
                return string.Empty;
            }

            Stack<string> names = new();
            Transform current = target;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names);
        }

        public readonly struct SelectedRendererTarget
        {
            public SelectedRendererTarget(GameObject rendererObject, GameObject authoringRoot)
            {
                RendererObject = rendererObject;
                AuthoringRoot = authoringRoot;
            }

            public GameObject RendererObject { get; }
            public GameObject AuthoringRoot { get; }
        }

        public static GameObject ResolveSelectedAuthoringRoot(GameObject selectedObject)
        {
            if (selectedObject == null)
            {
                return null;
            }

            PrefabStage stage = PrefabStageUtility.GetPrefabStage(selectedObject);
            if (stage != null && selectedObject.scene == stage.scene)
            {
                return stage.prefabContentsRoot;
            }

            if (!EditorUtility.IsPersistent(selectedObject))
            {
                GameObject outermostInstanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(selectedObject);
                if (outermostInstanceRoot != null)
                {
                    return outermostInstanceRoot;
                }
            }

            return selectedObject;
        }

        public static IEnumerable<SelectedRendererTarget> GetSelectedRendererTargets()
        {
            HashSet<int> seen = new();
            foreach (GameObject selected in Selection.gameObjects)
            {
                if (selected == null)
                {
                    continue;
                }

                GameObject authoringRoot = ResolveSelectedAuthoringRoot(selected);
                foreach (MeshRenderer renderer in selected.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (renderer == null || renderer.GetComponent<MeshFilter>() == null)
                    {
                        continue;
                    }

                    if (seen.Add(renderer.gameObject.GetInstanceID()))
                    {
                        yield return new SelectedRendererTarget(renderer.gameObject, authoringRoot);
                    }
                }
            }
        }

        public static IEnumerable<GameObject> GetSelectedRendererObjects()
        {
            return GetSelectedRendererTargets().Select(target => target.RendererObject);
        }

        public static string BuildPaintAssetPath(
            VertexPainterSettings settings,
            GameObject selectionTarget,
            Mesh sourceMesh,
            VertexPaintResolvedTarget ownershipTarget)
        {
            string root = NormalizeAssetPath(settings.GeneratedDataRoot);
            string category = ownershipTarget.CategoryName;
            string scope = SanitizeFileName(ownershipTarget.ScopeLabel);
            string hierarchy = SanitizeFileName(GetOwnershipHierarchyPath(selectionTarget, ownershipTarget));
            string meshKey = GetMeshKey(sourceMesh);
            string ownershipIdentity = GetGeneratedPaintAssetOwnershipIdentity(selectionTarget, ownershipTarget);
            string fullHash = Hash128.Compute($"{ownershipIdentity}|{meshKey}").ToString();
            string hash = fullHash.Length <= 10 ? fullHash : fullHash.Substring(0, 10);
            string fileName = $"{hierarchy}_{hash}.asset";

            return $"{root}/{category}/{scope}/{fileName}";
        }

        public static string GetSharedContainerTargetKey(VertexPaintResolvedTarget ownershipTarget)
        {
            return $"{NormalizeAssetPath(ownershipTarget.AssetPath)}|{ownershipTarget.RelativePath}";
        }

        public sealed class SharedContainerPaintSession : IDisposable
        {
            private sealed class PrefabContext
            {
                public string AssetPath;
                public PrefabUtility.EditPrefabContentsScope? Scope;
                public GameObject PrefabRoot;
                public bool IsDirty;
                public bool IsDisposed;

                public void DisposeScope()
                {
                    if (IsDisposed)
                    {
                        return;
                    }

                    if (Scope.HasValue)
                    {
                        Scope.Value.Dispose();
                    }

                    Scope = null;
                    PrefabRoot = null;
                    IsDisposed = true;
                }
            }

            private sealed class TargetState
            {
                public string Key;
                public PrefabContext Context;
                public GameObject OwnerObject;
                public MeshRenderer Renderer;
                public VertexPaintBinding Binding;
                public VertexPaintAsset PaintAsset;
                public VertexPaintAsset InheritedPaintAsset;
                public Color32[] Colors;
                public bool HasEditableAsset;
                public bool ColorsDirty;
                public bool BindingDirty;
            }

            private readonly string undoLabel;
            private readonly Dictionary<string, PrefabContext> prefabContexts = new();
            private readonly Dictionary<string, TargetState> targetStates = new();
            private readonly HashSet<VertexPaintAsset> cleanupCandidates = new();
            private bool disposed;

            public SharedContainerPaintSession(string undoLabel)
            {
                this.undoLabel = undoLabel;
            }

            public bool TryGetEditableColors(
                VertexPainterWindow.TargetDescriptor target,
                out Color32[] colors,
                out string error)
            {
                colors = null;
                if (!TryGetOrCreateTargetState(target, out TargetState state, out error))
                {
                    return false;
                }

                if (!state.HasEditableAsset)
                {
                    if (!TryEnsureBindingDirect(
                            state.OwnerObject,
                            target,
                            target.CurrentPaintTarget,
                            GetInitialColorsForNewOwnership(target, target.CurrentPaintTarget),
                            undoLabel,
                            false,
                            out VertexPaintBinding binding,
                            out VertexPaintAsset paintAsset,
                            out error))
                    {
                        return false;
                    }

                    state.Binding = binding;
                    state.Renderer = state.OwnerObject.GetComponent<MeshRenderer>();
                    state.PaintAsset = paintAsset;
                    state.Colors = paintAsset != null && paintAsset.MatchesSourceMesh(target.SourceMesh)
                        ? paintAsset.GetColorsCopy()
                        : CreateDefaultColorBuffer(target.SourceMesh.vertexCount);
                    state.HasEditableAsset = true;
                    state.BindingDirty = true;
                    state.Context.IsDirty = true;
                }

                colors = state.Colors;
                return true;
            }

            public bool TryClearToInherited(VertexPainterWindow.TargetDescriptor target, out string error)
            {
                if (!TryGetOrCreateTargetState(target, out TargetState state, out error))
                {
                    return false;
                }

                VertexPaintAsset previousPaintAsset = state.HasEditableAsset ? state.PaintAsset : null;
                if (state.Binding == null)
                {
                    if (state.InheritedPaintAsset == null)
                    {
                        return true;
                    }

                    state.Binding = state.OwnerObject.AddComponent<VertexPaintBinding>();
                    state.Renderer = state.OwnerObject.GetComponent<MeshRenderer>();
                }

                if (!TryAssignBindingPaintAsset(state.Binding, state.InheritedPaintAsset, false, out error))
                {
                    return false;
                }

                state.PaintAsset = state.InheritedPaintAsset;
                state.HasEditableAsset = false;
                state.BindingDirty = true;
                state.ColorsDirty = false;
                state.Context.IsDirty = true;
                state.Colors = state.InheritedPaintAsset != null && state.InheritedPaintAsset.MatchesSourceMesh(target.SourceMesh)
                    ? state.InheritedPaintAsset.GetColorsCopy()
                    : CreateDefaultColorBuffer(target.SourceMesh.vertexCount);

                if (previousPaintAsset != null && previousPaintAsset != state.InheritedPaintAsset)
                {
                    cleanupCandidates.Add(previousPaintAsset);
                }

                return true;
            }

            public void MarkDirty(VertexPainterWindow.TargetDescriptor target)
            {
                string key = GetSharedContainerTargetKey(target.CurrentPaintTarget);
                if (!targetStates.TryGetValue(key, out TargetState state))
                {
                    return;
                }

                state.ColorsDirty = true;
                state.Context.IsDirty = true;
            }

            public bool TryGetPendingColorBuffer(VertexPainterWindow.TargetDescriptor target, out Color32[] colors)
            {
                colors = null;
                if (!target.CurrentPaintTarget.IsAvailable ||
                    target.CurrentPaintTarget.EditsLiveObject ||
                    target.CurrentPaintTarget.Level != VertexPaintOwnershipLevel.ContainerPrefab)
                {
                    return false;
                }

                return targetStates.TryGetValue(GetSharedContainerTargetKey(target.CurrentPaintTarget), out TargetState state) &&
                       state.Colors != null &&
                       (colors = state.Colors) != null;
            }

            public bool Commit(out string error)
            {
                error = string.Empty;
                foreach (TargetState state in targetStates.Values)
                {
                    if (state.ColorsDirty && state.PaintAsset != null)
                    {
                        state.PaintAsset.OverwriteColors(state.Colors);
                        RebuildStreamMesh(state.PaintAsset);
                        EditorUtility.SetDirty(state.PaintAsset);
                        state.Context.IsDirty = true;
                    }

                    if (state.BindingDirty && state.Binding != null)
                    {
                        state.Binding.RefreshBinding();
                        MarkBindingStateDirty(state.Binding, state.Renderer);
                    }
                }

                List<PrefabContext> dirtyContexts = prefabContexts.Values
                    .Where(context => context.IsDirty)
                    .ToList();

                foreach (PrefabContext context in dirtyContexts)
                {
                    context.DisposeScope();
                }

                if (dirtyContexts.Count > 0)
                {
                    AssetDatabase.SaveAssets();
                    foreach (PrefabContext context in dirtyContexts)
                    {
                        AssetDatabase.ImportAsset(context.AssetPath, ImportAssetOptions.ForceUpdate);
                    }

                    RefreshLoadedBindings();
                }

                foreach (VertexPaintAsset paintAsset in cleanupCandidates)
                {
                    TryDeleteUnusedGeneratedPaintAsset(paintAsset, null, false);
                }

                cleanupCandidates.Clear();
                return true;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                foreach (PrefabContext context in prefabContexts.Values)
                {
                    context?.DisposeScope();
                }

                prefabContexts.Clear();
                targetStates.Clear();
                cleanupCandidates.Clear();
            }

            private bool TryGetOrCreateTargetState(
                VertexPainterWindow.TargetDescriptor target,
                out TargetState state,
                out string error)
            {
                error = string.Empty;
                state = null;

                VertexPaintResolvedTarget ownershipTarget = target.CurrentPaintTarget;
                if (!ownershipTarget.IsAvailable ||
                    ownershipTarget.EditsLiveObject ||
                    ownershipTarget.Level != VertexPaintOwnershipLevel.ContainerPrefab)
                {
                    error = ownershipTarget.IsAvailable
                        ? "This target does not use shared container editing."
                        : ownershipTarget.Reason;
                    return false;
                }

                string key = GetSharedContainerTargetKey(ownershipTarget);
                if (targetStates.TryGetValue(key, out state))
                {
                    return true;
                }

                if (!TryGetOrCreatePrefabContext(ownershipTarget, out PrefabContext context, out error))
                {
                    return false;
                }

                GameObject ownerObject = ResolveOwnerObject(context.PrefabRoot, ownershipTarget.RelativePath);
                if (ownerObject == null)
                {
                    error = $"Could not resolve '{ownershipTarget.RelativePath}' inside '{ownershipTarget.AssetPath}'.";
                    return false;
                }

                MeshFilter meshFilter = ownerObject.GetComponent<MeshFilter>();
                MeshRenderer renderer = ownerObject.GetComponent<MeshRenderer>();
                if (meshFilter == null || renderer == null || meshFilter.sharedMesh == null)
                {
                    error = "The target ownership object needs a MeshFilter, a MeshRenderer, and a valid source mesh.";
                    return false;
                }

                if (target.SourceMesh != null && meshFilter.sharedMesh != target.SourceMesh)
                {
                    error = "The target ownership object no longer matches the selected renderer's source mesh.";
                    return false;
                }

                VertexPaintBinding binding = ownerObject.GetComponent<VertexPaintBinding>();
                VertexPaintAsset inheritedPaintAsset = GetInheritedPaintAssetForClear(target, ownershipTarget);
                VertexPaintAsset paintAsset = binding != null && binding.PaintAsset != null && binding.PaintAsset.MatchesSourceMesh(target.SourceMesh)
                    ? binding.PaintAsset
                    : null;

                state = new TargetState
                {
                    Key = key,
                    Context = context,
                    OwnerObject = ownerObject,
                    Renderer = renderer,
                    Binding = binding,
                    PaintAsset = paintAsset,
                    InheritedPaintAsset = inheritedPaintAsset,
                    Colors = paintAsset != null && ownershipTarget.OwnsPaint
                        ? paintAsset.GetColorsCopy()
                        : inheritedPaintAsset != null && inheritedPaintAsset.MatchesSourceMesh(target.SourceMesh)
                            ? inheritedPaintAsset.GetColorsCopy()
                            : CreateDefaultColorBuffer(target.SourceMesh.vertexCount),
                    HasEditableAsset = ownershipTarget.OwnsPaint && paintAsset != null
                };

                targetStates[key] = state;
                return true;
            }

            private bool TryGetOrCreatePrefabContext(
                VertexPaintResolvedTarget ownershipTarget,
                out PrefabContext context,
                out string error)
            {
                error = string.Empty;
                context = null;
                string assetPath = NormalizeAssetPath(ownershipTarget.AssetPath);
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    error = "The target ownership asset path is invalid.";
                    return false;
                }

                if (prefabContexts.TryGetValue(assetPath, out context))
                {
                    return true;
                }

                context = new PrefabContext
                {
                    AssetPath = assetPath,
                    Scope = new PrefabUtility.EditPrefabContentsScope(assetPath)
                };
                context.PrefabRoot = context.Scope.Value.prefabContentsRoot;

                prefabContexts.Add(assetPath, context);
                return true;
            }
        }

        public static bool TryEnsureTargetPaintAsset(
            VertexPainterWindow.TargetDescriptor target,
            VertexPaintResolvedTarget ownershipTarget,
            string undoLabel,
            out VertexPaintAsset paintAsset,
            out string error)
        {
            paintAsset = null;

            if (!ownershipTarget.IsAvailable)
            {
                error = ownershipTarget.Reason;
                return false;
            }

            if (target.GameObject == null || target.SourceMesh == null)
            {
                error = "The selected object needs a MeshFilter, a MeshRenderer, and a valid source mesh.";
                return false;
            }

            if (TryUseExistingTargetPaintAsset(target.SourceMesh, ownershipTarget, undoLabel, out paintAsset, out error))
            {
                return true;
            }

            Color32[] initialColors = GetInitialColorsForNewOwnership(target, ownershipTarget);

            if (ownershipTarget.EditsLiveObject)
            {
                return TryEnsureBindingDirect(ownershipTarget.ResolvedObject, target, ownershipTarget, initialColors, undoLabel, true, out _, out paintAsset, out error);
            }

            VertexPaintAsset createdPaintAsset = null;
            string createError = string.Empty;
            bool createdOrUpdated = TryEditResolvedTargetObject(
                ownershipTarget,
                ownerObject =>
                {
                    return TryEnsureBindingDirect(ownerObject, target, ownershipTarget, initialColors, undoLabel, false, out _, out createdPaintAsset, out createError);
                },
                out error);

            paintAsset = createdPaintAsset;
            if (!createdOrUpdated && string.IsNullOrWhiteSpace(error))
            {
                error = createError;
            }

            if (createdOrUpdated)
            {
                RefreshLoadedBindings();
            }

            return createdOrUpdated;
        }

        public static bool TryClearTargetOwnership(
            VertexPainterWindow.TargetDescriptor target,
            VertexPaintResolvedTarget ownershipTarget,
            string undoLabel,
            out string error)
        {
            error = string.Empty;
            if (!ownershipTarget.IsAvailable)
            {
                error = ownershipTarget.Reason;
                return false;
            }

            if (!ownershipTarget.OwnsPaint && !ownershipTarget.SuppressesInheritedPaint)
            {
                return true;
            }

            VertexPaintAsset previousPaintAsset = ownershipTarget.PaintAsset;
            VertexPaintAsset inheritedPaintAsset = GetInheritedPaintAssetForClear(target, ownershipTarget);
            bool useUndo = ownershipTarget.EditsLiveObject;

            string clearError = string.Empty;
            bool cleared = useUndo
                ? TryClearTargetOwnershipDirect(ownershipTarget.ResolvedObject, inheritedPaintAsset, undoLabel, true, out error)
                : TryEditResolvedTargetObject(
                    ownershipTarget,
                    ownerObject => TryClearTargetOwnershipDirect(ownerObject, inheritedPaintAsset, undoLabel, false, out clearError),
                    out error);

            if (!cleared && string.IsNullOrWhiteSpace(error))
            {
                error = clearError;
            }

            if (!cleared)
            {
                return false;
            }

            RefreshLoadedBindings();

            if (previousPaintAsset != null)
            {
                TryDeleteUnusedGeneratedPaintAsset(previousPaintAsset, null, false);
            }

            return true;
        }

        public static bool TryCopyColorBufferToTarget(
            VertexPainterWindow.TargetDescriptor target,
            VertexPaintResolvedTarget ownershipTarget,
            Color32[] colors,
            string undoLabel,
            out string error)
        {
            error = string.Empty;
            if (colors == null)
            {
                error = "The source color buffer is missing.";
                return false;
            }

            if (!TryEnsureTargetPaintAsset(target, ownershipTarget, undoLabel, out VertexPaintAsset paintAsset, out error))
            {
                return false;
            }

            if (colors.Length != paintAsset.SourceVertexCount)
            {
                error = "The source color buffer does not match this mesh.";
                return false;
            }

            Undo.RecordObject(paintAsset, undoLabel);
            paintAsset.OverwriteColors(colors);
            RebuildStreamMesh(paintAsset);
            EditorUtility.SetDirty(paintAsset);

            if (ownershipTarget.EditsLiveObject)
            {
                RefreshBindingIfLiveTarget(ownershipTarget);
            }
            else
            {
                AssetDatabase.SaveAssets();
                if (!string.IsNullOrWhiteSpace(ownershipTarget.AssetPath))
                {
                    AssetDatabase.ImportAsset(ownershipTarget.AssetPath, ImportAssetOptions.ForceUpdate);
                }

                RefreshLoadedBindings();
            }

            return true;
        }

        public static bool TryClearTargetOwnershipLevels(
            VertexPainterWindow.TargetDescriptor target,
            IEnumerable<VertexPaintOwnershipLevel> ownershipLevels,
            string undoLabel,
            out string error)
        {
            error = string.Empty;
            if (ownershipLevels == null)
            {
                return true;
            }

            foreach (VertexPaintOwnershipLevel ownershipLevel in ownershipLevels)
            {
                VertexPaintResolvedTarget ownershipTarget = target.GetOwnershipTarget(ownershipLevel);
                if (!ownershipTarget.IsAvailable)
                {
                    continue;
                }

                if (!TryClearTargetOwnership(target, ownershipTarget, undoLabel, out error))
                {
                    return false;
                }
            }

            return true;
        }

        public static void EnsureStreamMesh(VertexPaintAsset paintAsset, string undoLabel, bool useUndo = true)
        {
            if (paintAsset == null)
            {
                return;
            }

            Mesh streamMesh = paintAsset.StreamMesh;
            if (streamMesh == null)
            {
                streamMesh = new Mesh
                {
                    name = $"{paintAsset.name}_Stream",
                    hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector
                };

                if (useUndo)
                {
                    Undo.RegisterCreatedObjectUndo(streamMesh, undoLabel);
                }

                AssetDatabase.AddObjectToAsset(streamMesh, paintAsset);

                if (useUndo)
                {
                    Undo.RecordObject(paintAsset, undoLabel);
                }

                paintAsset.AssignStreamMesh(streamMesh);
            }

            RebuildStreamMesh(paintAsset);
        }

        public static void RebuildStreamMesh(VertexPaintAsset paintAsset)
        {
            if (paintAsset == null || paintAsset.StreamMesh == null)
            {
                return;
            }

            Mesh streamMesh = paintAsset.StreamMesh;
            Color32[] colors = paintAsset.GetColorsCopy();

            streamMesh.Clear(false);
            streamMesh.SetVertexBufferParams(colors.Length, StreamDescriptor);
            if (colors.Length > 0)
            {
                streamMesh.SetVertexBufferData(colors, 0, 0, colors.Length, 0, MeshUpdateFlags.DontResetBoneBounds);
            }

            streamMesh.bounds = paintAsset.SourceMesh != null
                ? paintAsset.SourceMesh.bounds
                : new Bounds(Vector3.zero, Vector3.one);

            EditorUtility.SetDirty(streamMesh);
            EditorUtility.SetDirty(paintAsset);
        }

        public static void RefreshLoadedBindings()
        {
            foreach (VertexPaintBinding binding in Resources.FindObjectsOfTypeAll<VertexPaintBinding>())
            {
                if (binding == null)
                {
                    continue;
                }

                VertexPaintAsset asset = binding.PaintAsset;
                if (asset != null && asset.SourceMesh != null && asset.HasColorData)
                {
                    RebuildStreamMesh(asset);
                }

                binding.RefreshBinding();
            }
        }

        public static void RefreshBindingIfLiveTarget(VertexPaintResolvedTarget ownershipTarget)
        {
            if (!ownershipTarget.EditsLiveObject || ownershipTarget.ResolvedObject == null)
            {
                return;
            }

            VertexPaintBinding binding = ownershipTarget.ResolvedObject.GetComponent<VertexPaintBinding>();
            if (binding == null)
            {
                return;
            }

            binding.RefreshBinding();
            EditorUtility.SetDirty(binding);
        }

        public static bool TryDeleteUnusedGeneratedPaintAsset(VertexPaintAsset paintAsset, VertexPaintBinding ignoredBinding)
        {
            return TryDeleteUnusedGeneratedPaintAsset(paintAsset, ignoredBinding, false);
        }

        private static bool TryDeleteUnusedGeneratedPaintAsset(
            VertexPaintAsset paintAsset,
            VertexPaintBinding ignoredBinding,
            bool includeSavedSceneAndPrefabReferences)
        {
            if (paintAsset == null || !IsGeneratedPaintAsset(paintAsset))
            {
                return false;
            }

            string assetPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(paintAsset));
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            if (HasOtherLoadedBindingReferences(paintAsset, ignoredBinding))
            {
                return false;
            }

            if (includeSavedSceneAndPrefabReferences && HasSavedSceneOrPrefabReferences(paintAsset))
            {
                return false;
            }

            bool deleted = AssetDatabase.DeleteAsset(assetPath);
            if (deleted)
            {
                CleanupEmptyGeneratedFolders(VertexPainterSettings.instance.GeneratedDataRoot);
            }

            return deleted;
        }

        public static bool TryCleanupUnusedGeneratedPaintAssets(
            string undoLabel,
            out int deletedCount,
            out int keptCount,
            out string error)
        {
            deletedCount = 0;
            keptCount = 0;
            error = string.Empty;

            VertexPainterSettings settings = VertexPainterSettings.instance;
            if (!settings.TryValidateGeneratedDataRoot(out error))
            {
                return false;
            }

            string generatedRoot = NormalizeAssetPath(settings.GeneratedDataRoot);
            if (string.IsNullOrWhiteSpace(generatedRoot) || !AssetDatabase.IsValidFolder(generatedRoot))
            {
                return true;
            }

            string[] assetGuids = AssetDatabase.FindAssets("t:VertexPaintAsset", new[] { generatedRoot });
            if (assetGuids == null || assetGuids.Length == 0)
            {
                return true;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoLabel);

            foreach (string assetGuid in assetGuids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(assetGuid);
                VertexPaintAsset paintAsset = AssetDatabase.LoadAssetAtPath<VertexPaintAsset>(assetPath);
                if (TryDeleteUnusedGeneratedPaintAsset(paintAsset, null, true))
                {
                    deletedCount++;
                }
                else
                {
                    keptCount++;
                }
            }

            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            CleanupEmptyGeneratedFolders(generatedRoot);
            return true;
        }

        public static bool TryGetHit(
            SceneView sceneView,
            IReadOnlyList<VertexPainterWindow.TargetDescriptor> targets,
            out VertexPainterWindow.PaintHit hitResult)
        {
            hitResult = default;
            if (sceneView == null || targets == null || targets.Count == 0)
            {
                return false;
            }

            Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
            bool foundHit = false;
            float nearestDistance = float.MaxValue;

            for (int i = 0; i < targets.Count; i++)
            {
                VertexPainterWindow.TargetDescriptor target = targets[i];
                if (!target.IsGeometryValid)
                {
                    continue;
                }

                if (!TryIntersectRayMesh(
                        ray,
                        target.SourceMesh,
                        target.Renderer.localToWorldMatrix,
                        out Vector3 point,
                        out Vector3 normal,
                        out float distance,
                        out int triangleIndex,
                        out Vector3 barycentricCoordinate))
                {
                    continue;
                }

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    hitResult = new VertexPainterWindow.PaintHit(
                        target,
                        point,
                        normal,
                        distance,
                        triangleIndex,
                        barycentricCoordinate);
                    foundHit = true;
                }
            }

            return foundHit;
        }

        private static string GetOwnershipHierarchyPath(GameObject selectionTarget, VertexPaintResolvedTarget ownershipTarget)
        {
            if (!string.IsNullOrWhiteSpace(ownershipTarget.RelativePath))
            {
                return ownershipTarget.RelativePath;
            }

            return selectionTarget != null ? GetHierarchyPath(selectionTarget.transform) : "Unnamed";
        }

        private static bool TryUseExistingTargetPaintAsset(
            Mesh sourceMesh,
            VertexPaintResolvedTarget ownershipTarget,
            string undoLabel,
            out VertexPaintAsset paintAsset,
            out string error)
        {
            if (!ownershipTarget.OwnsPaint)
            {
                paintAsset = null;
                error = string.Empty;
                return false;
            }

            paintAsset = ownershipTarget.PaintAsset;
            if (paintAsset == null)
            {
                error = string.Empty;
                return false;
            }

            if (!paintAsset.MatchesSourceMesh(sourceMesh))
            {
                if (paintAsset.SourceMesh == null)
                {
                    Undo.RecordObject(paintAsset, undoLabel);
                    paintAsset.Initialize(sourceMesh, VertexPaintChannelUtility.DefaultColor);
                }
                else
                {
                    error = paintAsset.GetValidationMessage(sourceMesh);
                    return false;
                }
            }

            EnsureStreamMesh(paintAsset, undoLabel, true);
            error = string.Empty;
            return true;
        }

        private static bool TryEnsureBindingDirect(
            GameObject ownerObject,
            VertexPainterWindow.TargetDescriptor selectionTarget,
            VertexPaintResolvedTarget ownershipTarget,
            Color32[] initialColors,
            string undoLabel,
            bool useUndo,
            out VertexPaintBinding binding,
            out VertexPaintAsset paintAsset,
            out string error)
        {
            binding = null;
            paintAsset = null;

            if (ownerObject == null)
            {
                error = "The target ownership object could not be resolved.";
                return false;
            }

            MeshFilter meshFilter = ownerObject.GetComponent<MeshFilter>();
            MeshRenderer renderer = ownerObject.GetComponent<MeshRenderer>();
            if (meshFilter == null || renderer == null || meshFilter.sharedMesh == null)
            {
                error = "The target ownership object needs a MeshFilter, a MeshRenderer, and a valid source mesh.";
                return false;
            }

            if (selectionTarget.SourceMesh != null && meshFilter.sharedMesh != selectionTarget.SourceMesh)
            {
                error = "The target ownership object no longer matches the selected renderer's source mesh.";
                return false;
            }

            VertexPaintBinding existingBinding = ownerObject.GetComponent<VertexPaintBinding>();
            if (!TryEnsurePaintAsset(
                    selectionTarget.GameObject,
                    meshFilter.sharedMesh,
                    ownershipTarget,
                    ownershipTarget.OwnsPaint && existingBinding != null ? existingBinding.PaintAsset : null,
                    initialColors,
                    undoLabel,
                    useUndo,
                    out paintAsset,
                    out error))
            {
                return false;
            }

            binding = existingBinding;
            if (binding == null)
            {
                if (useUndo)
                {
                    binding = Undo.AddComponent<VertexPaintBinding>(ownerObject);
                }
                else
                {
                    binding = ownerObject.AddComponent<VertexPaintBinding>();
                }
            }

            if (useUndo)
            {
                Undo.RecordObject(binding, undoLabel);
                Undo.RecordObject(renderer, undoLabel);
            }

            if (!TryAssignBindingPaintAsset(binding, paintAsset, useUndo, out error))
            {
                return false;
            }

            binding.RefreshBinding();
            MarkBindingStateDirty(binding, renderer);
            return true;
        }

        private static bool TryEnsurePaintAsset(
            GameObject selectionTarget,
            Mesh sourceMesh,
            VertexPaintResolvedTarget ownershipTarget,
            VertexPaintAsset existingAsset,
            Color32[] initialColors,
            string undoLabel,
            bool useUndo,
            out VertexPaintAsset paintAsset,
            out string error)
        {
            paintAsset = null;
            VertexPainterSettings settings = VertexPainterSettings.instance;
            if (!settings.TryValidateGeneratedDataRoot(out error))
            {
                return false;
            }

            if (existingAsset != null)
            {
                paintAsset = existingAsset;
                if (!paintAsset.MatchesSourceMesh(sourceMesh))
                {
                    if (paintAsset.SourceMesh == null)
                    {
                        if (useUndo)
                        {
                            Undo.RecordObject(paintAsset, undoLabel);
                        }

                        paintAsset.Initialize(sourceMesh, VertexPaintChannelUtility.DefaultColor);
                    }
                    else
                    {
                        error = paintAsset.GetValidationMessage(sourceMesh);
                        return false;
                    }
                }

                EnsureStreamMesh(paintAsset, undoLabel, useUndo);
                error = string.Empty;
                return true;
            }

            string assetPath = BuildPaintAssetPath(settings, selectionTarget, sourceMesh, ownershipTarget);
            EnsureFolderExists(Path.GetDirectoryName(assetPath));

            paintAsset = AssetDatabase.LoadAssetAtPath<VertexPaintAsset>(assetPath);
            if (paintAsset == null)
            {
                paintAsset = ScriptableObject.CreateInstance<VertexPaintAsset>();
                paintAsset.name = Path.GetFileNameWithoutExtension(assetPath);
                paintAsset.Initialize(sourceMesh, VertexPaintChannelUtility.DefaultColor);
                AssetDatabase.CreateAsset(paintAsset, assetPath);

                if (useUndo)
                {
                    Undo.RegisterCreatedObjectUndo(paintAsset, undoLabel);
                }
            }

            if (!paintAsset.MatchesSourceMesh(sourceMesh))
            {
                if (paintAsset.SourceMesh == null)
                {
                    if (useUndo)
                    {
                        Undo.RecordObject(paintAsset, undoLabel);
                    }

                    paintAsset.Initialize(sourceMesh, VertexPaintChannelUtility.DefaultColor);
                }
                else
                {
                    error = paintAsset.GetValidationMessage(sourceMesh);
                    return false;
                }
            }

            if (initialColors != null)
            {
                if (paintAsset.SourceVertexCount != initialColors.Length)
                {
                    error = "The initial color buffer does not match this mesh.";
                    return false;
                }

                if (useUndo)
                {
                    Undo.RecordObject(paintAsset, undoLabel);
                }

                paintAsset.OverwriteColors(initialColors);
            }

            EnsureStreamMesh(paintAsset, undoLabel, useUndo);
            error = string.Empty;
            return true;
        }

        private static Color32[] GetInitialColorsForNewOwnership(
            VertexPainterWindow.TargetDescriptor target,
            VertexPaintResolvedTarget ownershipTarget)
        {
            if (ownershipTarget.OwnsPaint || target.SourceMesh == null)
            {
                return null;
            }

            if (TryGetVisibleColorBuffer(target, out Color32[] visibleColors))
            {
                return visibleColors;
            }

            return CreateDefaultColorBuffer(target.SourceMesh.vertexCount);
        }

        private static bool TryGetVisibleColorBuffer(
            VertexPainterWindow.TargetDescriptor target,
            out Color32[] colors)
        {
            colors = null;
            if (!target.VisiblePaintOwner.HasValue)
            {
                return false;
            }

            VertexPaintResolvedTarget visibleTarget = target.GetOwnershipTarget(target.VisiblePaintOwner.Value);
            VertexPaintAsset visiblePaintAsset = visibleTarget.PaintAsset;
            if (visiblePaintAsset == null || !visiblePaintAsset.MatchesSourceMesh(target.SourceMesh))
            {
                return false;
            }

            colors = visiblePaintAsset.GetColorsCopy();
            return colors.Length == target.SourceMesh.vertexCount;
        }

        private static Color32[] CreateDefaultColorBuffer(int vertexCount)
        {
            if (vertexCount <= 0)
            {
                return Array.Empty<Color32>();
            }

            Color32[] colors = new Color32[vertexCount];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = VertexPaintChannelUtility.DefaultColor;
            }

            return colors;
        }

        private static bool TryClearTargetOwnershipDirect(
            GameObject ownerObject,
            VertexPaintAsset inheritedPaintAsset,
            string undoLabel,
            bool useUndo,
            out string error)
        {
            error = string.Empty;
            if (ownerObject == null)
            {
                return true;
            }

            VertexPaintBinding binding = ownerObject.GetComponent<VertexPaintBinding>();
            if (binding == null)
            {
                ClearRendererAdditionalVertexStreams(ownerObject.GetComponent<MeshRenderer>(), undoLabel, useUndo);
                return true;
            }

            MeshRenderer renderer = ownerObject.GetComponent<MeshRenderer>();

            if (useUndo)
            {
                Undo.RecordObject(binding, undoLabel);
                if (renderer != null)
                {
                    Undo.RecordObject(renderer, undoLabel);
                }
            }

            if (!TryAssignBindingPaintAsset(binding, inheritedPaintAsset, useUndo, out error))
            {
                return false;
            }

            binding.RefreshBinding();
            MarkBindingStateDirty(binding, renderer);
            return true;
        }

        private static VertexPaintAsset GetInheritedPaintAssetForClear(
            VertexPainterWindow.TargetDescriptor target,
            VertexPaintResolvedTarget ownershipTarget)
        {
            return ownershipTarget.Level switch
            {
                VertexPaintOwnershipLevel.SceneInstance => target.ContainerTarget.IsAvailable && target.ContainerTarget.OwnsPaint
                    ? target.ContainerTarget.PaintAsset
                    : target.FoundationTarget.IsAvailable && target.FoundationTarget.HasPaint
                        ? target.FoundationTarget.PaintAsset
                        : null,
                VertexPaintOwnershipLevel.ContainerPrefab => target.FoundationTarget.IsAvailable && target.FoundationTarget.HasPaint
                    ? target.FoundationTarget.PaintAsset
                    : null,
                _ => null
            };
        }

        private static bool TryAssignBindingPaintAsset(
            VertexPaintBinding binding,
            VertexPaintAsset paintAsset,
            bool useUndo,
            out string error)
        {
            error = string.Empty;
            if (binding == null)
            {
                error = "The target binding could not be resolved.";
                return false;
            }

            SerializedObject serializedBinding = new(binding);
            SerializedProperty paintAssetProperty = serializedBinding.FindProperty(PaintAssetPropertyName);
            if (paintAssetProperty == null)
            {
                error = "The target binding is missing its serialized paint asset property.";
                return false;
            }

            serializedBinding.Update();
            paintAssetProperty.objectReferenceValue = paintAsset;

            if (useUndo)
            {
                serializedBinding.ApplyModifiedProperties();
            }
            else
            {
                serializedBinding.ApplyModifiedPropertiesWithoutUndo();
            }

            return true;
        }

        private static void ClearRendererAdditionalVertexStreams(MeshRenderer renderer, string undoLabel, bool useUndo = true)
        {
            if (renderer == null)
            {
                return;
            }

            if (useUndo)
            {
                Undo.RecordObject(renderer, undoLabel);
            }

            renderer.additionalVertexStreams = null;
            EditorUtility.SetDirty(renderer);
        }

        private static void MarkBindingStateDirty(VertexPaintBinding binding, MeshRenderer renderer)
        {
            if (binding != null)
            {
                EditorUtility.SetDirty(binding);
            }

            if (renderer != null)
            {
                EditorUtility.SetDirty(renderer);
            }
        }

        private static bool TryEditResolvedTargetObject(
            VertexPaintResolvedTarget ownershipTarget,
            Func<GameObject, bool> editAction,
            out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(ownershipTarget.AssetPath))
            {
                error = "The target ownership asset path is invalid.";
                return false;
            }

            using (PrefabUtility.EditPrefabContentsScope editScope = new(ownershipTarget.AssetPath))
            {
                GameObject prefabRoot = editScope.prefabContentsRoot;
                GameObject ownerObject = ResolveOwnerObject(prefabRoot, ownershipTarget.RelativePath);
                if (ownerObject == null)
                {
                    error = $"Could not resolve '{ownershipTarget.RelativePath}' inside '{ownershipTarget.AssetPath}'.";
                    return false;
                }

                if (!editAction(ownerObject))
                {
                    return false;
                }

                AssetDatabase.SaveAssets();
            }

            AssetDatabase.ImportAsset(ownershipTarget.AssetPath, ImportAssetOptions.ForceUpdate);

            return true;
        }

        private static GameObject ResolveOwnerObject(GameObject prefabRoot, string relativePath)
        {
            if (prefabRoot == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return prefabRoot;
            }

            Transform current = prefabRoot.transform;
            string[] segments = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                if (!TryParseRelativePathSegment(segment, out string childName, out int siblingOrdinal))
                {
                    return null;
                }

                current = FindChildByNameAndOrdinal(current, childName, siblingOrdinal);
                if (current == null)
                {
                    return null;
                }
            }

            return current.gameObject;
        }

        private static bool TryParseRelativePathSegment(string segment, out string childName, out int siblingOrdinal)
        {
            childName = string.Empty;
            siblingOrdinal = 0;

            if (string.IsNullOrWhiteSpace(segment))
            {
                return false;
            }

            int openBracketIndex = segment.LastIndexOf('[');
            bool hasOrdinalSuffix =
                openBracketIndex > 0 &&
                segment.EndsWith("]", StringComparison.Ordinal) &&
                int.TryParse(segment.Substring(openBracketIndex + 1, segment.Length - openBracketIndex - 2), out siblingOrdinal);

            if (!hasOrdinalSuffix)
            {
                childName = segment;
                siblingOrdinal = 0;
                return true;
            }

            childName = segment.Substring(0, openBracketIndex);
            return !string.IsNullOrWhiteSpace(childName) && siblingOrdinal >= 0;
        }

        private static Transform FindChildByNameAndOrdinal(Transform parent, string childName, int siblingOrdinal)
        {
            if (parent == null || string.IsNullOrWhiteSpace(childName) || siblingOrdinal < 0)
            {
                return null;
            }

            int currentOrdinal = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name != childName)
                {
                    continue;
                }

                if (currentOrdinal == siblingOrdinal)
                {
                    return child;
                }

                currentOrdinal++;
            }

            return null;
        }

        private static void EnsureFolderExists(string folderPath)
        {
            string normalized = NormalizeAssetPath(folderPath);
            if (string.IsNullOrWhiteSpace(normalized) || AssetDatabase.IsValidFolder(normalized))
            {
                return;
            }

            string parent = NormalizeAssetPath(Path.GetDirectoryName(normalized));
            if (!string.IsNullOrWhiteSpace(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolderExists(parent);
            }

            string leaf = Path.GetFileName(normalized);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Unnamed";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(value.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray())
                .Replace(" ", "_");
        }

        private static string GetMeshKey(Mesh mesh)
        {
            if (mesh == null)
            {
                return "MissingMesh";
            }

            string assetPath = AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                return $"{assetPath}|{mesh.name}|{mesh.vertexCount}";
            }

            return $"{mesh.name}|{mesh.vertexCount}|{mesh.GetInstanceID()}";
        }

        private static string GetGeneratedPaintAssetOwnershipIdentity(
            GameObject selectionTarget,
            VertexPaintResolvedTarget ownershipTarget)
        {
            if (!ownershipTarget.IsAvailable)
            {
                return "Unavailable";
            }

            if (ownershipTarget.Level == VertexPaintOwnershipLevel.ContainerPrefab ||
                ownershipTarget.Level == VertexPaintOwnershipLevel.FoundationPrefab)
            {
                string assetPath = NormalizeAssetPath(ownershipTarget.AssetPath);
                string relativePath = GetOwnershipHierarchyPath(selectionTarget, ownershipTarget);
                return $"{assetPath}|{relativePath}";
            }

            return $"{ownershipTarget.ScopeKey}|{ownershipTarget.ObjectKey}";
        }

        private static bool IsGeneratedPaintAsset(VertexPaintAsset paintAsset)
        {
            if (paintAsset == null)
            {
                return false;
            }

            string assetPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(paintAsset));
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            string generatedRoot = NormalizeAssetPath(VertexPainterSettings.instance.GeneratedDataRoot);
            return assetPath == generatedRoot || assetPath.StartsWith($"{generatedRoot}/");
        }

        private static bool HasOtherLoadedBindingReferences(VertexPaintAsset paintAsset, VertexPaintBinding ignoredBinding)
        {
            foreach (VertexPaintBinding binding in Resources.FindObjectsOfTypeAll<VertexPaintBinding>())
            {
                if (binding == null || binding == ignoredBinding)
                {
                    continue;
                }

                if (binding.PaintAsset == paintAsset)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasSavedSceneOrPrefabReferences(VertexPaintAsset paintAsset)
        {
            string assetPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(paintAsset));
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            string assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrWhiteSpace(assetGuid))
            {
                return false;
            }

            foreach (string candidatePath in EnumeratePotentialReferenceAssetPaths())
            {
                if (string.Equals(candidatePath, assetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string absolutePath = ToAbsoluteProjectPath(candidatePath);
                if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
                {
                    continue;
                }

                try
                {
                    string contents = File.ReadAllText(absolutePath);
                    if (contents.IndexOf(assetGuid, StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return false;
        }

        private static IEnumerable<string> EnumeratePotentialReferenceAssetPaths()
        {
            foreach (string sceneGuid in AssetDatabase.FindAssets("t:Scene"))
            {
                string scenePath = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(sceneGuid));
                if (!string.IsNullOrWhiteSpace(scenePath))
                {
                    yield return scenePath;
                }
            }

            foreach (string prefabGuid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string prefabPath = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(prefabGuid));
                if (!string.IsNullOrWhiteSpace(prefabPath))
                {
                    yield return prefabPath;
                }
            }
        }

        private static string ToAbsoluteProjectPath(string assetPath)
        {
            string normalizedAssetPath = NormalizeAssetPath(assetPath);
            if (string.IsNullOrWhiteSpace(normalizedAssetPath) || !normalizedAssetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return string.Empty;
            }

            return Path.Combine(projectRoot, normalizedAssetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void CleanupEmptyGeneratedFolders(string generatedRoot)
        {
            string normalizedRoot = NormalizeAssetPath(generatedRoot);
            if (string.IsNullOrWhiteSpace(normalizedRoot) || !AssetDatabase.IsValidFolder(normalizedRoot))
            {
                return;
            }

            List<string> folders = GetGeneratedFoldersDeepestFirst(normalizedRoot);
            foreach (string folder in folders)
            {
                if (string.Equals(folder, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!AssetDatabase.IsValidFolder(folder) || !IsGeneratedFolderEmpty(folder))
                {
                    continue;
                }

                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static List<string> GetGeneratedFoldersDeepestFirst(string rootFolder)
        {
            List<string> folders = new() { rootFolder };
            CollectGeneratedFoldersRecursive(rootFolder, folders);
            folders.Sort((left, right) => right.Length.CompareTo(left.Length));
            return folders;
        }

        private static void CollectGeneratedFoldersRecursive(string folder, List<string> results)
        {
            foreach (string childFolder in AssetDatabase.GetSubFolders(folder))
            {
                results.Add(childFolder);
                CollectGeneratedFoldersRecursive(childFolder, results);
            }
        }

        private static bool IsGeneratedFolderEmpty(string folder)
        {
            string absoluteFolderPath = ToAbsoluteProjectPath(folder);
            if (string.IsNullOrWhiteSpace(absoluteFolderPath) || !Directory.Exists(absoluteFolderPath))
            {
                return true;
            }

            bool hasNonMetaFiles = Directory.EnumerateFiles(absoluteFolderPath)
                .Any(file => !string.Equals(Path.GetExtension(file), ".meta", StringComparison.OrdinalIgnoreCase));

            return !hasNonMetaFiles && AssetDatabase.GetSubFolders(folder).Length == 0;
        }

        private static bool TryIntersectRayMesh(
            Ray worldRay,
            Mesh mesh,
            Matrix4x4 localToWorld,
            out Vector3 point,
            out Vector3 normal,
            out float distance,
            out int triangleIndex,
            out Vector3 barycentricCoordinate)
        {
            point = Vector3.zero;
            normal = Vector3.up;
            distance = 0f;
            triangleIndex = -1;
            barycentricCoordinate = Vector3.zero;
            if (mesh == null)
            {
                return false;
            }

            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length < 3)
            {
                return false;
            }

            Matrix4x4 worldToLocal = localToWorld.inverse;
            Vector3 localOrigin = worldToLocal.MultiplyPoint3x4(worldRay.origin);
            Vector3 localDirection = worldToLocal.MultiplyVector(worldRay.direction);
            if (localDirection.sqrMagnitude <= Mathf.Epsilon)
            {
                return false;
            }

            localDirection.Normalize();
            Ray localRay = new(localOrigin, localDirection);

            bool foundHit = false;
            float nearestWorldDistance = float.MaxValue;
            Vector3 nearestPoint = Vector3.zero;
            Vector3 nearestNormal = Vector3.up;
            int nearestTriangleIndex = -1;
            Vector3 nearestBarycentric = Vector3.zero;

            for (int triangleOffset = 0; triangleOffset <= triangles.Length - 3; triangleOffset += 3)
            {
                int index0 = triangles[triangleOffset];
                int index1 = triangles[triangleOffset + 1];
                int index2 = triangles[triangleOffset + 2];
                if (index0 < 0 || index1 < 0 || index2 < 0 ||
                    index0 >= vertices.Length || index1 >= vertices.Length || index2 >= vertices.Length)
                {
                    continue;
                }

                Vector3 vertex0 = vertices[index0];
                Vector3 vertex1 = vertices[index1];
                Vector3 vertex2 = vertices[index2];

                if (!TryIntersectTriangle(localRay, vertex0, vertex1, vertex2, out float localDistance, out Vector3 barycentric))
                {
                    continue;
                }

                Vector3 localPoint = localOrigin + localDirection * localDistance;
                Vector3 worldPoint = localToWorld.MultiplyPoint3x4(localPoint);
                float worldDistance = Vector3.Distance(worldRay.origin, worldPoint);
                if (worldDistance >= nearestWorldDistance)
                {
                    continue;
                }

                Vector3 localNormal = Vector3.Cross(vertex1 - vertex0, vertex2 - vertex0);
                Vector3 worldNormal = worldToLocal.transpose.MultiplyVector(localNormal).normalized;
                if (worldNormal.sqrMagnitude <= Mathf.Epsilon)
                {
                    worldNormal = Vector3.up;
                }
                else if (Vector3.Dot(worldNormal, worldRay.direction) > 0f)
                {
                    worldNormal = -worldNormal;
                }

                nearestWorldDistance = worldDistance;
                nearestPoint = worldPoint;
                nearestNormal = worldNormal;
                nearestTriangleIndex = triangleOffset / 3;
                nearestBarycentric = barycentric;
                foundHit = true;
            }

            point = nearestPoint;
            normal = nearestNormal;
            distance = nearestWorldDistance;
            triangleIndex = nearestTriangleIndex;
            barycentricCoordinate = nearestBarycentric;
            return foundHit;
        }

        private static bool TryIntersectTriangle(
            Ray ray,
            Vector3 vertex0,
            Vector3 vertex1,
            Vector3 vertex2,
            out float distance,
            out Vector3 barycentric)
        {
            const float Epsilon = 0.000001f;

            distance = 0f;
            barycentric = Vector3.zero;

            Vector3 edge1 = vertex1 - vertex0;
            Vector3 edge2 = vertex2 - vertex0;
            Vector3 p = Vector3.Cross(ray.direction, edge2);
            float determinant = Vector3.Dot(edge1, p);
            if (Mathf.Abs(determinant) < Epsilon)
            {
                return false;
            }

            float inverseDeterminant = 1f / determinant;
            Vector3 t = ray.origin - vertex0;
            float u = Vector3.Dot(t, p) * inverseDeterminant;
            if (u < 0f || u > 1f)
            {
                return false;
            }

            Vector3 q = Vector3.Cross(t, edge1);
            float v = Vector3.Dot(ray.direction, q) * inverseDeterminant;
            if (v < 0f || u + v > 1f)
            {
                return false;
            }

            float hitDistance = Vector3.Dot(edge2, q) * inverseDeterminant;
            if (hitDistance < 0f)
            {
                return false;
            }

            distance = hitDistance;
            barycentric = new Vector3(1f - u - v, u, v);
            return true;
        }
    }
}

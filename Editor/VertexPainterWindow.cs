using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace JuiceAI.VertexPainter.Editor
{
    public sealed class VertexPainterWindow : EditorWindow
    {
        private sealed class ScenePreviewCacheEntry
        {
            public int SourceMeshId;
            public Mesh Mesh;
            public Color32[] Colors;
        }

        private struct ActionReport
        {
            public int Applied;
            public int Unsupported;
            public int MissingSource;
            public int AlreadyThere;
            public int Warnings;
        }

        public enum BrushMode
        {
            Paint = 0,
            Erase = 1,
            Sample = 2
        }

        public readonly struct TargetDescriptor
        {
            public TargetDescriptor(
                GameObject gameObject,
                GameObject authoringRoot,
                MeshFilter meshFilter,
                MeshRenderer renderer,
                Mesh sourceMesh,
                VertexPaintResolvedTarget sceneTarget,
                VertexPaintResolvedTarget containerTarget,
                VertexPaintResolvedTarget foundationTarget,
                VertexPaintResolvedTarget currentPaintTarget,
                VertexPaintOwnershipLevel? visiblePaintOwner,
                IVertexPaintMaterialAdapter adapter)
            {
                GameObject = gameObject;
                AuthoringRoot = authoringRoot;
                MeshFilter = meshFilter;
                Renderer = renderer;
                SourceMesh = sourceMesh;
                SceneTarget = sceneTarget;
                ContainerTarget = containerTarget;
                FoundationTarget = foundationTarget;
                CurrentPaintTarget = currentPaintTarget;
                VisiblePaintOwner = visiblePaintOwner;
                Adapter = adapter;
            }

            public GameObject GameObject { get; }
            public GameObject AuthoringRoot { get; }
            public MeshFilter MeshFilter { get; }
            public MeshRenderer Renderer { get; }
            public Mesh SourceMesh { get; }
            public VertexPaintResolvedTarget SceneTarget { get; }
            public VertexPaintResolvedTarget ContainerTarget { get; }
            public VertexPaintResolvedTarget FoundationTarget { get; }
            public VertexPaintResolvedTarget CurrentPaintTarget { get; }
            public VertexPaintOwnershipLevel? VisiblePaintOwner { get; }
            public IVertexPaintMaterialAdapter Adapter { get; }
            public bool IsGeometryValid => GameObject != null && MeshFilter != null && Renderer != null && SourceMesh != null;
            public bool IsPaintable => IsGeometryValid && CurrentPaintTarget.IsAvailable;

            public VertexPaintResolvedTarget GetOwnershipTarget(VertexPaintOwnershipLevel level)
            {
                return level switch
                {
                    VertexPaintOwnershipLevel.SceneInstance => SceneTarget,
                    VertexPaintOwnershipLevel.ContainerPrefab => ContainerTarget,
                    VertexPaintOwnershipLevel.FoundationPrefab => FoundationTarget,
                    _ => default
                };
            }
        }

        public readonly struct PaintHit
        {
            public PaintHit(
                TargetDescriptor target,
                Vector3 point,
                Vector3 normal,
                float distance,
                int triangleIndex,
                Vector3 barycentricCoordinate)
            {
                Target = target;
                Point = point;
                Normal = normal;
                Distance = distance;
                TriangleIndex = triangleIndex;
                BarycentricCoordinate = barycentricCoordinate;
            }

            public TargetDescriptor Target { get; }
            public Vector3 Point { get; }
            public Vector3 Normal { get; }
            public float Distance { get; }
            public int TriangleIndex { get; }
            public Vector3 BarycentricCoordinate { get; }
        }

        private BrushMode brushMode;
        private VertexPaintChannel activeChannel = VertexPaintChannel.Red;
        private float brushSize;
        private float brushStrength;
        private float brushSpacing;
        private float brushFalloff;
        private float lastSampledValue = -1f;

        private bool strokeInProgress;
        private bool strokeHasStampPoint;
        private int strokeUndoGroup = -1;
        private Vector3 strokeLastStampPoint;
        private readonly Dictionary<int, ScenePreviewCacheEntry> scenePreviewEntries = new();
        private VertexPaintEditorUtility.SharedContainerPaintSession activeSharedContainerPaintSession;

        private static Material scenePreviewMaterial;

        [MenuItem("Tools/Juice AI/Vertex Painter")]
        public static void ShowWindow()
        {
            VertexPainterWindow window = GetWindow<VertexPainterWindow>();
            window.titleContent = new GUIContent("Vertex Painter");
            window.minSize = new Vector2(380f, 480f);
        }

        private void OnEnable()
        {
            VertexPainterSettings settings = VertexPainterSettings.instance;
            brushSize = Mathf.Max(settings.DefaultBrushSize, 0.01f);
            brushStrength = Mathf.Clamp01(settings.DefaultBrushStrength);
            brushSpacing = Mathf.Clamp(settings.DefaultBrushSpacing, 0.05f, 1f);
            brushFalloff = Mathf.Max(settings.DefaultBrushFalloff, 0.01f);

            SceneView.duringSceneGui += DuringSceneGui;
            Selection.selectionChanged += HandleSelectionChanged;
            Undo.undoRedoPerformed += HandleUndoRedo;
            HandleSelectionChanged();
        }

        private void OnDisable()
        {
            if (strokeInProgress)
            {
                EndStroke();
            }

            DisposeActiveSharedContainerPaintSession();
            ClearScenePreviewEntries();
            SceneView.duringSceneGui -= DuringSceneGui;
            Selection.selectionChanged -= HandleSelectionChanged;
            Undo.undoRedoPerformed -= HandleUndoRedo;
        }

        private void OnGUI()
        {
            List<TargetDescriptor> targets = CollectTargets().ToList();

            DrawSelectionSummary(targets);
            EditorGUILayout.Space();
            DrawBrushControls();
            EditorGUILayout.Space();
            DrawActionButtons(targets);
            EditorGUILayout.Space();
            DrawSettings();
            EditorGUILayout.Space();
            DrawContextDetails(targets);
        }

        private void DrawSelectionSummary(IReadOnlyList<TargetDescriptor> targets)
        {
            if (targets.Count == 0)
            {
                EditorGUILayout.HelpBox("Select one or more MeshRenderer targets in the hierarchy or scene to start painting.", MessageType.Info);
                return;
            }

            int geometryCount = targets.Count(target => target.IsGeometryValid);
            TargetDescriptor primary = GetPrimaryTarget(targets);
            EditorGUILayout.LabelField("Selection", BuildSelectionSummary(primary, geometryCount));
        }

        private void DrawContextDetails(IReadOnlyList<TargetDescriptor> targets)
        {
            if (targets.Count == 0)
            {
                return;
            }

            int geometryCount = targets.Count(target => target.IsGeometryValid);
            int paintableCount = targets.Count(target => target.IsPaintable);
            TargetDescriptor primary = GetPrimaryTarget(targets);

            if (!primary.CurrentPaintTarget.IsAvailable && !string.IsNullOrWhiteSpace(primary.CurrentPaintTarget.Reason))
            {
                EditorGUILayout.HelpBox(primary.CurrentPaintTarget.Reason, MessageType.Warning);
            }

            if (primary.Adapter != null)
            {
                Material[] materials = primary.Renderer.sharedMaterials ?? Array.Empty<Material>();
                string description = primary.Adapter.GetDescription(primary.Renderer, materials);
                if (!string.IsNullOrWhiteSpace(description))
                {
                    EditorGUILayout.HelpBox(description, MessageType.Info);
                }

                string warning = primary.Adapter.GetWarning(primary.Renderer, materials);
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);
                }
            }

            int unpaintableCount = geometryCount - paintableCount;
            if (unpaintableCount > 0)
            {
                EditorGUILayout.HelpBox(
                    paintableCount > 0
                        ? $"{unpaintableCount} selected mesh target(s) are not paintable in the current context. Brush strokes will affect the {paintableCount} target(s) that are."
                        : "None of the selected mesh targets are paintable in the current context.",
                    MessageType.None);
            }

            if (lastSampledValue >= 0f)
            {
                EditorGUILayout.LabelField("Last Sample", lastSampledValue.ToString("0.###"));
            }
        }

        private static TargetDescriptor GetPrimaryTarget(IReadOnlyList<TargetDescriptor> targets)
        {
            TargetDescriptor primary = targets.FirstOrDefault(target => target.IsGeometryValid);
            if (!primary.IsGeometryValid)
            {
                primary = targets[0];
            }

            return primary;
        }

        private static string BuildSelectionSummary(TargetDescriptor primary, int meshTargetCount)
        {
            GameObject selectedObject = Selection.activeGameObject != null
                ? Selection.activeGameObject
                : primary.GameObject;

            string currentName = selectedObject != null && !string.IsNullOrWhiteSpace(selectedObject.name)
                ? selectedObject.name
                : primary.GameObject != null
                    ? primary.GameObject.name
                    : "None";

            string originalPrefabName = GetOriginalPrefabName(selectedObject);
            string objectLabel = !string.IsNullOrWhiteSpace(originalPrefabName) && originalPrefabName != currentName
                ? $"{currentName} (Original Prefab: {originalPrefabName})"
                : currentName;

            return $"{objectLabel} • {meshTargetCount} mesh target{(meshTargetCount == 1 ? string.Empty : "s")}";
        }

        private static string GetOriginalPrefabName(GameObject selectedObject)
        {
            if (selectedObject == null)
            {
                return string.Empty;
            }

            GameObject sourceObject = PrefabUtility.GetCorrespondingObjectFromSource(selectedObject);
            if (sourceObject != null && !string.IsNullOrWhiteSpace(sourceObject.name))
            {
                return sourceObject.name;
            }

            PrefabStage stage = PrefabStageUtility.GetPrefabStage(selectedObject);
            if (stage != null && selectedObject.scene == stage.scene && stage.prefabContentsRoot != null)
            {
                return stage.prefabContentsRoot.name;
            }

            return string.Empty;
        }

        private void DrawBrushControls()
        {
            EditorGUILayout.LabelField("Brush", EditorStyles.boldLabel);
            brushMode = (BrushMode)GUILayout.Toolbar((int)brushMode, new[] { "Paint", "Erase", "Sample" });
            activeChannel = (VertexPaintChannel)EditorGUILayout.EnumPopup("Channel", activeChannel);

            VertexPainterSettings settings = VertexPainterSettings.instance;
            EditorGUI.BeginChangeCheck();
            brushSize = EditorGUILayout.Slider(new GUIContent("Size", "World-space brush radius."), brushSize, 0.05f, 16f);
            brushStrength = EditorGUILayout.Slider(new GUIContent("Strength", "How strongly each stamp moves the active channel toward paint or erase."), brushStrength, 0.01f, 1f);
            brushSpacing = EditorGUILayout.Slider(new GUIContent("Spacing", "Distance between brush stamps as a fraction of brush size. Lower values stamp more often."), brushSpacing, 0.05f, 1f);
            brushFalloff = EditorGUILayout.Slider(new GUIContent("Falloff Exponent", "Lower values give a softer edge. Higher values keep more strength near the center and fade faster near the edge."), brushFalloff, 0.1f, 8f);
            if (EditorGUI.EndChangeCheck())
            {
                settings.DefaultBrushSize = brushSize;
                settings.DefaultBrushStrength = brushStrength;
                settings.DefaultBrushSpacing = brushSpacing;
                settings.DefaultBrushFalloff = brushFalloff;
                settings.SaveIfDirty();
            }
        }

        private void DrawActionButtons(IReadOnlyList<TargetDescriptor> targets)
        {
            EditorGUILayout.LabelField("Current Context", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fill 0"))
                {
                    FillSelection(targets, 0);
                }

                if (GUILayout.Button("Fill 1"))
                {
                    FillSelection(targets, 255);
                }
            }

            if (GUILayout.Button("Clear Current Paint"))
            {
                ClearCurrentPaint(targets);
            }
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            VertexPainterSettings settings = VertexPainterSettings.instance;

            EditorGUI.BeginChangeCheck();
            string generatedRoot = EditorGUILayout.TextField("Generated Data Root", settings.GeneratedDataRoot);
            if (EditorGUI.EndChangeCheck())
            {
                settings.GeneratedDataRoot = generatedRoot;
                settings.SaveIfDirty();
            }

            if (!settings.TryValidateGeneratedDataRoot(out string error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Warning);
            }

            EditorGUI.BeginChangeCheck();
            bool scenePreviewEnabled = EditorGUILayout.Toggle("Scene Preview", settings.ScenePreviewEnabled);
            float scenePreviewOpacity = EditorGUILayout.Slider("Preview Opacity", settings.ScenePreviewOpacity, 0.05f, 0.8f);
            if (EditorGUI.EndChangeCheck())
            {
                settings.ScenePreviewEnabled = scenePreviewEnabled;
                settings.ScenePreviewOpacity = scenePreviewOpacity;
                settings.SaveIfDirty();
            }

            if (GUILayout.Button("Clean Orphan Paint Assets"))
            {
                CleanOrphanPaintAssets();
            }
        }

        private void DuringSceneGui(SceneView sceneView)
        {
            List<TargetDescriptor> targets = CollectTargets()
                .Where(target => target.IsPaintable)
                .ToList();

            if (targets.Count == 0)
            {
                return;
            }

            DrawScenePreview(targets);

            if (!VertexPaintEditorUtility.TryGetHit(sceneView, targets, out PaintHit hit))
            {
                return;
            }

            DrawBrushPreview(hit);

            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            Event currentEvent = Event.current;

            switch (currentEvent.type)
            {
                case EventType.Layout:
                    HandleUtility.AddDefaultControl(controlId);
                    break;
                case EventType.MouseDown:
                    if (CanBeginStroke(currentEvent))
                    {
                        GUIUtility.hotControl = controlId;
                        if (brushMode == BrushMode.Sample)
                        {
                            SampleHit(hit);
                        }
                        else
                        {
                            BeginStroke(targets);
                            ApplyBrushAlongStroke(hit.Point, targets, true);
                        }

                        currentEvent.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlId && strokeInProgress && brushMode != BrushMode.Sample)
                    {
                        ApplyBrushAlongStroke(hit.Point, targets, false);
                        currentEvent.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        if (strokeInProgress)
                        {
                            EndStroke();
                        }

                        GUIUtility.hotControl = 0;
                        currentEvent.Use();
                    }
                    break;
            }
        }

        private IEnumerable<TargetDescriptor> CollectTargets()
        {
            foreach (VertexPaintEditorUtility.SelectedRendererTarget selectedTarget in VertexPaintEditorUtility.GetSelectedRendererTargets())
            {
                GameObject targetObject = selectedTarget.RendererObject;
                MeshFilter meshFilter = targetObject.GetComponent<MeshFilter>();
                MeshRenderer renderer = targetObject.GetComponent<MeshRenderer>();
                Mesh sourceMesh = meshFilter != null ? meshFilter.sharedMesh : null;
                VertexPaintResolvedTarget sceneTarget = VertexPaintAuthoringContextUtility.Evaluate(targetObject, VertexPaintOwnershipLevel.SceneInstance, selectedTarget.AuthoringRoot);
                VertexPaintResolvedTarget containerTarget = VertexPaintAuthoringContextUtility.Evaluate(targetObject, VertexPaintOwnershipLevel.ContainerPrefab, selectedTarget.AuthoringRoot);
                VertexPaintResolvedTarget foundationTarget = VertexPaintAuthoringContextUtility.Evaluate(targetObject, VertexPaintOwnershipLevel.FoundationPrefab, selectedTarget.AuthoringRoot);
                sceneTarget = VertexPaintAuthoringContextUtility.NormalizeSceneTarget(sceneTarget, containerTarget, foundationTarget);
                VertexPaintResolvedTarget currentPaintTarget = VertexPaintAuthoringContextUtility.ResolveCurrentPaintTarget(
                    targetObject,
                    sceneTarget,
                    containerTarget,
                    foundationTarget);
                VertexPaintOwnershipLevel? visiblePaintOwner = VertexPaintAuthoringContextUtility.GetVisiblePaintOwner(sceneTarget, containerTarget, foundationTarget);
                IVertexPaintMaterialAdapter adapter = renderer != null ? VertexPaintMaterialAdapterRegistry.FindBestAdapter(renderer) : null;

                yield return new TargetDescriptor(
                    targetObject,
                    selectedTarget.AuthoringRoot,
                    meshFilter,
                    renderer,
                    sourceMesh,
                    sceneTarget,
                    containerTarget,
                    foundationTarget,
                    currentPaintTarget,
                    visiblePaintOwner,
                    adapter);
            }
        }

        private void HandleSelectionChanged()
        {
            ClearScenePreviewEntries();
            lastSampledValue = -1f;

            TargetDescriptor primary = CollectTargets().FirstOrDefault(target => target.IsGeometryValid && target.Adapter != null);
            if (primary.IsGeometryValid && primary.Adapter != null)
            {
                activeChannel = primary.Adapter.GetDefaultChannel(primary.Renderer, primary.Renderer.sharedMaterials);
            }

            Repaint();
            SceneView.RepaintAll();
        }

        private void HandleUndoRedo()
        {
            VertexPaintEditorUtility.RefreshLoadedBindings();
            Repaint();
            SceneView.RepaintAll();
        }

        private VertexPaintEditorUtility.SharedContainerPaintSession EnsureActiveSharedContainerPaintSession()
        {
            return activeSharedContainerPaintSession ??= new VertexPaintEditorUtility.SharedContainerPaintSession("Vertex Paint Stroke");
        }

        private void DisposeActiveSharedContainerPaintSession()
        {
            activeSharedContainerPaintSession?.Dispose();
            activeSharedContainerPaintSession = null;
        }

        private static bool UsesSharedContainerSession(TargetDescriptor target)
        {
            return target.CurrentPaintTarget.IsAvailable &&
                   !target.CurrentPaintTarget.EditsLiveObject &&
                   target.CurrentPaintTarget.Level == VertexPaintOwnershipLevel.ContainerPrefab;
        }

        private void BeginStroke(IReadOnlyList<TargetDescriptor> targets)
        {
            strokeInProgress = true;
            strokeHasStampPoint = false;
            strokeUndoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Vertex Paint Stroke");

            if (targets.Any(UsesSharedContainerSession))
            {
                activeSharedContainerPaintSession = new VertexPaintEditorUtility.SharedContainerPaintSession("Vertex Paint Stroke");
            }
        }

        private void EndStroke()
        {
            if (activeSharedContainerPaintSession != null &&
                !activeSharedContainerPaintSession.Commit(out string error) &&
                !string.IsNullOrWhiteSpace(error))
            {
                Debug.LogWarning(error);
                ShowNotification(new GUIContent(error));
            }

            DisposeActiveSharedContainerPaintSession();
            strokeInProgress = false;
            strokeHasStampPoint = false;
            if (strokeUndoGroup >= 0)
            {
                Undo.CollapseUndoOperations(strokeUndoGroup);
            }

            strokeUndoGroup = -1;
        }

        private static bool CanBeginStroke(Event currentEvent)
        {
            return currentEvent.button == 0 &&
                   !currentEvent.alt &&
                   !EditorGUIUtility.editingTextField;
        }

        private void DrawBrushPreview(PaintHit hit)
        {
            Color brushColor = brushMode switch
            {
                BrushMode.Paint => new Color(0.25f, 1f, 0.35f, 0.9f),
                BrushMode.Erase => new Color(1f, 0.35f, 0.25f, 0.9f),
                _ => new Color(1f, 0.95f, 0.35f, 0.9f)
            };

            Vector3 normal = hit.Normal.sqrMagnitude > 0f
                ? hit.Normal.normalized
                : GetSceneViewNormalFallback();

            Handles.color = brushColor;
            Handles.DrawWireDisc(hit.Point, normal, brushSize);

            float falloffRadius = GetBrushFalloffPreviewRadius(brushSize, brushFalloff, 0.5f);
            if (falloffRadius > 0f && falloffRadius < brushSize)
            {
                Handles.color = new Color(brushColor.r, brushColor.g, brushColor.b, brushColor.a * 0.45f);
                Handles.DrawWireDisc(hit.Point, normal, falloffRadius);
            }
        }

        private static float GetBrushFalloffPreviewRadius(float radius, float falloffExponent, float normalizedWeight)
        {
            if (radius <= 0f || falloffExponent <= 0f)
            {
                return 0f;
            }

            float clampedWeight = Mathf.Clamp01(normalizedWeight);
            if (clampedWeight <= 0f)
            {
                return radius;
            }

            if (clampedWeight >= 1f)
            {
                return 0f;
            }

            // The brush uses weight = brushStrength * (1 - distance / radius)^falloffExponent.
            // This preview ring marks where the falloff term reaches 50% of the center weight.
            float normalizedRadius = 1f - Mathf.Pow(clampedWeight, 1f / falloffExponent);
            return Mathf.Clamp01(normalizedRadius) * radius;
        }

        private static Vector3 GetSceneViewNormalFallback()
        {
            SceneView currentSceneView = SceneView.currentDrawingSceneView;
            if (currentSceneView != null && currentSceneView.camera != null)
            {
                return currentSceneView.camera.transform.forward;
            }

            return Vector3.up;
        }

        private void SampleHit(PaintHit hit)
        {
            if (!TryGetVisibleOrCurrentColorBuffer(hit.Target, true, out Color32[] colors))
            {
                return;
            }

            int sampledIndex = GetSampledVertexIndex(hit.Target.SourceMesh, hit.TriangleIndex, hit.BarycentricCoordinate);
            if (sampledIndex < 0 || sampledIndex >= colors.Length)
            {
                return;
            }

            lastSampledValue = VertexPaintChannelUtility.GetValue01(colors[sampledIndex], activeChannel);
            Repaint();
        }

        private void FillSelection(IReadOnlyList<TargetDescriptor> allTargets, byte value)
        {
            List<TargetDescriptor> targets = allTargets.Where(target => target.IsPaintable).ToList();
            if (targets.Count == 0)
            {
                ShowNotification(new GUIContent("Select at least one paintable mesh target."));
                return;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Vertex Paint Fill");
            VertexPaintEditorUtility.SharedContainerPaintSession sharedSession = null;
            HashSet<string> processedSharedTargets = new();

            try
            {
                foreach (TargetDescriptor target in targets)
                {
                    VertexPaintResolvedTarget paintTarget = target.CurrentPaintTarget;
                    if (UsesSharedContainerSession(target))
                    {
                        string sharedTargetKey = VertexPaintEditorUtility.GetSharedContainerTargetKey(paintTarget);
                        if (!processedSharedTargets.Add(sharedTargetKey))
                        {
                            continue;
                        }

                        if (!TryGetVisibleOrCurrentColorBuffer(target, true, out Color32[] sourceColors))
                        {
                            continue;
                        }

                        Color32[] nextColors = (Color32[])sourceColors.Clone();
                        bool targetChanged = false;
                        for (int i = 0; i < nextColors.Length; i++)
                        {
                            Color32 next = VertexPaintChannelUtility.WithByte(nextColors[i], activeChannel, value);
                            if (!next.Equals(nextColors[i]))
                            {
                                nextColors[i] = next;
                                targetChanged = true;
                            }
                        }

                        if (!targetChanged)
                        {
                            continue;
                        }

                        sharedSession ??= new VertexPaintEditorUtility.SharedContainerPaintSession("Vertex Paint Fill");
                        if (!sharedSession.TryGetEditableColors(target, out Color32[] editableColors, out string sharedError))
                        {
                            if (!string.IsNullOrWhiteSpace(sharedError))
                            {
                                Debug.LogWarning(sharedError, target.GameObject);
                            }

                            continue;
                        }

                        Array.Copy(nextColors, editableColors, nextColors.Length);
                        sharedSession.MarkDirty(target);
                        continue;
                    }

                    if (!VertexPaintEditorUtility.TryEnsureTargetPaintAsset(
                            target,
                            paintTarget,
                            "Vertex Paint Fill",
                            out VertexPaintAsset paintAsset,
                            out string error))
                    {
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            Debug.LogWarning(error, target.GameObject);
                        }

                        continue;
                    }

                    Color32[] colors = paintAsset.GetColorsCopy();
                    if (colors.Length == 0)
                    {
                        continue;
                    }

                    for (int i = 0; i < colors.Length; i++)
                    {
                        colors[i] = VertexPaintChannelUtility.WithByte(colors[i], activeChannel, value);
                    }

                    Undo.RecordObject(paintAsset, "Vertex Paint Fill");
                    paintAsset.OverwriteColors(colors);
                    VertexPaintEditorUtility.RebuildStreamMesh(paintAsset);
                    VertexPaintEditorUtility.RefreshBindingIfLiveTarget(paintTarget);
                    EditorUtility.SetDirty(paintAsset);
                }

                if (sharedSession != null &&
                    !sharedSession.Commit(out string commitError) &&
                    !string.IsNullOrWhiteSpace(commitError))
                {
                    Debug.LogWarning(commitError);
                }
            }
            finally
            {
                sharedSession?.Dispose();
            }

            Undo.CollapseUndoOperations(group);
            SceneView.RepaintAll();
        }

        private void ClearCurrentPaint(IReadOnlyList<TargetDescriptor> allTargets)
        {
            List<TargetDescriptor> targets = allTargets.Where(target => target.IsPaintable).ToList();
            if (targets.Count == 0)
            {
                ShowNotification(new GUIContent("Select at least one paintable mesh target."));
                return;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Clear Current Vertex Paint");
            VertexPaintEditorUtility.SharedContainerPaintSession sharedSession = null;
            HashSet<string> processedSharedTargets = new();

            try
            {
                foreach (TargetDescriptor target in targets)
                {
                    if (UsesSharedContainerSession(target))
                    {
                        if (!target.CurrentPaintTarget.OwnsPaint && !target.CurrentPaintTarget.SuppressesInheritedPaint)
                        {
                            continue;
                        }

                        string sharedTargetKey = VertexPaintEditorUtility.GetSharedContainerTargetKey(target.CurrentPaintTarget);
                        if (!processedSharedTargets.Add(sharedTargetKey))
                        {
                            continue;
                        }

                        sharedSession ??= new VertexPaintEditorUtility.SharedContainerPaintSession("Clear Current Vertex Paint");
                        if (!sharedSession.TryClearToInherited(target, out string sharedError) &&
                            !string.IsNullOrWhiteSpace(sharedError))
                        {
                            Debug.LogWarning(sharedError, target.GameObject);
                        }

                        continue;
                    }

                    if (!VertexPaintEditorUtility.TryClearTargetOwnership(
                            target,
                            target.CurrentPaintTarget,
                            "Clear Current Vertex Paint",
                            out string error) &&
                        !string.IsNullOrWhiteSpace(error))
                    {
                        Debug.LogWarning(error, target.GameObject);
                    }
                }

                if (sharedSession != null &&
                    !sharedSession.Commit(out string commitError) &&
                    !string.IsNullOrWhiteSpace(commitError))
                {
                    Debug.LogWarning(commitError);
                }
            }
            finally
            {
                sharedSession?.Dispose();
            }

            Undo.CollapseUndoOperations(group);
            SceneView.RepaintAll();
            ShowNotification(new GUIContent("Cleared current paint for the supported selection."));
        }

        private void PushScenePaintToContainer(IReadOnlyList<TargetDescriptor> allTargets)
        {
            List<TargetDescriptor> targets = allTargets.Where(target => target.IsGeometryValid).ToList();
            if (targets.Count == 0)
            {
                ShowNotification(new GUIContent("Select at least one mesh target first."));
                return;
            }

            ActionReport report = default;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Push Scene To Container Prefab");

            foreach (TargetDescriptor target in targets)
            {
                if (!target.SceneTarget.IsAvailable || !target.ContainerTarget.IsAvailable)
                {
                    report.Unsupported++;
                    continue;
                }

                if (!target.SceneTarget.OwnsPaint)
                {
                    if (target.VisiblePaintOwner == VertexPaintOwnershipLevel.ContainerPrefab)
                    {
                        report.AlreadyThere++;
                    }
                    else
                    {
                        report.MissingSource++;
                    }

                    continue;
                }

                if (!TryGetTargetColorBuffer(target, target.SceneTarget, false, out Color32[] sceneColors))
                {
                    report.MissingSource++;
                    continue;
                }

                if (!VertexPaintEditorUtility.TryCopyColorBufferToTarget(
                        target,
                        target.ContainerTarget,
                        sceneColors,
                        "Push Scene To Container Prefab",
                        out string error))
                {
                    report.Warnings++;
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        Debug.LogWarning(error, target.GameObject);
                    }

                    continue;
                }

                report.Applied++;
            }

            Undo.CollapseUndoOperations(group);
            SceneView.RepaintAll();
            ShowActionSummary("Push Scene To Container Prefab", report);
        }

        private void PullFromContainerPrefab(IReadOnlyList<TargetDescriptor> allTargets)
        {
            List<TargetDescriptor> targets = allTargets.Where(target => target.IsGeometryValid).ToList();
            if (targets.Count == 0)
            {
                ShowNotification(new GUIContent("Select at least one mesh target first."));
                return;
            }

            ActionReport report = default;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Pull From Container Prefab");

            foreach (TargetDescriptor target in targets)
            {
                if (!target.SceneTarget.IsAvailable || !target.ContainerTarget.IsAvailable)
                {
                    report.Unsupported++;
                    continue;
                }

                bool hasSceneOverride = target.SceneTarget.OwnsPaint || target.SceneTarget.SuppressesInheritedPaint;
                if (!hasSceneOverride)
                {
                    report.AlreadyThere++;
                    continue;
                }

                if (!VertexPaintEditorUtility.TryClearTargetOwnership(
                        target,
                        target.SceneTarget,
                        "Pull From Container Prefab",
                        out string error))
                {
                    report.Warnings++;
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        Debug.LogWarning(error, target.GameObject);
                    }

                    continue;
                }

                report.Applied++;
            }

            Undo.CollapseUndoOperations(group);
            SceneView.RepaintAll();
            ShowActionSummary("Pull From Container Prefab", report);
        }

        private void CleanOrphanPaintAssets()
        {
            if (!VertexPaintEditorUtility.TryCleanupUnusedGeneratedPaintAssets(
                    "Clean Orphan Vertex Paint Assets",
                    out int deletedCount,
                    out int keptCount,
                    out string error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Debug.LogWarning(error);
                    ShowNotification(new GUIContent(error));
                }

                return;
            }

            string message = deletedCount > 0
                ? $"Cleaned orphan paint assets: {deletedCount} deleted, {keptCount} kept."
                : $"No orphan paint assets found. {keptCount} referenced asset(s) kept.";
            ShowNotification(new GUIContent(message));
        }

        private void ApplyBrushAlongStroke(Vector3 worldPoint, IReadOnlyList<TargetDescriptor> targets, bool forceFirstStamp)
        {
            float stampSpacing = Mathf.Max(0.01f, brushSize * brushSpacing);
            if (!strokeHasStampPoint || forceFirstStamp)
            {
                ApplyBrushStamp(worldPoint, targets);
                strokeLastStampPoint = worldPoint;
                strokeHasStampPoint = true;
                return;
            }

            Vector3 segment = worldPoint - strokeLastStampPoint;
            float segmentLength = segment.magnitude;
            if (segmentLength < stampSpacing)
            {
                return;
            }

            Vector3 direction = segment / segmentLength;
            int stampCount = Mathf.FloorToInt(segmentLength / stampSpacing);
            for (int i = 1; i <= stampCount; i++)
            {
                Vector3 stampPoint = strokeLastStampPoint + direction * (stampSpacing * i);
                ApplyBrushStamp(stampPoint, targets);
            }

            float coveredLength = stampSpacing * stampCount;
            strokeLastStampPoint += direction * coveredLength;
        }

        private void ApplyBrushStamp(Vector3 worldCenter, IReadOnlyList<TargetDescriptor> targets)
        {
            byte targetByte = brushMode == BrushMode.Paint ? byte.MaxValue : (byte)0;
            bool anyChanged = false;
            HashSet<string> processedSharedTargets = new();

            foreach (TargetDescriptor target in targets)
            {
                if (!target.CurrentPaintTarget.IsAvailable)
                {
                    continue;
                }

                if (UsesSharedContainerSession(target))
                {
                    string sharedTargetKey = VertexPaintEditorUtility.GetSharedContainerTargetKey(target.CurrentPaintTarget);
                    if (!processedSharedTargets.Add(sharedTargetKey))
                    {
                        continue;
                    }

                    if (!TryGetVisibleOrCurrentColorBuffer(target, true, out Color32[] sourceColors))
                    {
                        continue;
                    }

                    Color32[] nextColors = (Color32[])sourceColors.Clone();
                    if (!TryApplyBrushToColorBuffer(target, worldCenter, targetByte, nextColors, out bool sharedTargetChanged))
                    {
                        continue;
                    }

                    if (!sharedTargetChanged)
                    {
                        continue;
                    }

                    VertexPaintEditorUtility.SharedContainerPaintSession sharedSession = EnsureActiveSharedContainerPaintSession();
                    if (!sharedSession.TryGetEditableColors(target, out Color32[] editableColors, out string sharedError))
                    {
                        if (!string.IsNullOrWhiteSpace(sharedError))
                        {
                            Debug.LogWarning(sharedError, target.GameObject);
                        }

                        continue;
                    }

                    Array.Copy(nextColors, editableColors, nextColors.Length);
                    sharedSession.MarkDirty(target);
                    anyChanged = true;
                    continue;
                }

                if (!VertexPaintEditorUtility.TryEnsureTargetPaintAsset(
                        target,
                        target.CurrentPaintTarget,
                        "Vertex Paint Stroke",
                        out VertexPaintAsset paintAsset,
                        out string error))
                {
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        Debug.LogWarning(error, target.GameObject);
                    }

                    continue;
                }

                Color32[] colors = paintAsset.GetColorsCopy();
                if (!TryApplyBrushToColorBuffer(target, worldCenter, targetByte, colors, out bool targetChanged))
                {
                    continue;
                }

                if (!targetChanged)
                {
                    continue;
                }

                anyChanged = true;
                Undo.RecordObject(paintAsset, "Vertex Paint Stroke");
                paintAsset.OverwriteColors(colors);
                VertexPaintEditorUtility.RebuildStreamMesh(paintAsset);
                VertexPaintEditorUtility.RefreshBindingIfLiveTarget(target.CurrentPaintTarget);
                EditorUtility.SetDirty(paintAsset);
            }

            if (anyChanged)
            {
                SceneView.RepaintAll();
            }
        }

        private bool TryApplyBrushToColorBuffer(
            TargetDescriptor target,
            Vector3 worldCenter,
            byte targetByte,
            Color32[] colors,
            out bool targetChanged)
        {
            targetChanged = false;
            Vector3[] vertices = target.SourceMesh.vertices;
            if (vertices == null || vertices.Length == 0 || colors == null || colors.Length != vertices.Length)
            {
                return false;
            }

            Matrix4x4 localToWorld = target.Renderer.localToWorldMatrix;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 worldVertex = localToWorld.MultiplyPoint3x4(vertices[i]);
                float distance = Vector3.Distance(worldCenter, worldVertex);
                if (distance > brushSize)
                {
                    continue;
                }

                float normalized = Mathf.Clamp01(distance / brushSize);
                float falloff = Mathf.Pow(1f - normalized, brushFalloff);
                float weight = falloff * brushStrength;

                Color32 next = VertexPaintChannelUtility.MoveToward(colors[i], activeChannel, targetByte, weight);
                if (!next.Equals(colors[i]))
                {
                    colors[i] = next;
                    targetChanged = true;
                }
            }

            return true;
        }

        private bool TryGetTargetColorBuffer(
            TargetDescriptor target,
            VertexPaintResolvedTarget ownershipTarget,
            bool allowDefault,
            out Color32[] colors)
        {
            colors = null;
            if (!target.IsGeometryValid)
            {
                return false;
            }

            if (activeSharedContainerPaintSession != null &&
                activeSharedContainerPaintSession.TryGetPendingColorBuffer(target, out colors))
            {
                return colors != null && colors.Length > 0;
            }

            if (ownershipTarget.PaintAsset != null && ownershipTarget.PaintAsset.MatchesSourceMesh(target.SourceMesh))
            {
                colors = ownershipTarget.PaintAsset.GetColorsCopy();
                return colors.Length > 0;
            }

            if (!allowDefault)
            {
                return false;
            }

            colors = new Color32[target.SourceMesh.vertexCount];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = VertexPaintChannelUtility.DefaultColor;
            }

            return true;
        }

        private bool TryGetVisibleColorBuffer(
            TargetDescriptor target,
            out Color32[] colors,
            out VertexPaintOwnershipLevel sourceLevel)
        {
            colors = null;
            sourceLevel = default;

            if (!target.VisiblePaintOwner.HasValue)
            {
                return false;
            }

            sourceLevel = target.VisiblePaintOwner.Value;
            return TryGetTargetColorBuffer(target, target.GetOwnershipTarget(sourceLevel), false, out colors);
        }

        private bool TryGetVisibleOrCurrentColorBuffer(TargetDescriptor target, bool allowDefault, out Color32[] colors)
        {
            colors = null;
            if (activeSharedContainerPaintSession != null &&
                activeSharedContainerPaintSession.TryGetPendingColorBuffer(target, out colors))
            {
                return colors != null && colors.Length > 0;
            }

            if (TryGetVisibleColorBuffer(target, out colors, out _))
            {
                return true;
            }

            if (target.CurrentPaintTarget.OwnsPaint)
            {
                return TryGetTargetColorBuffer(target, target.CurrentPaintTarget, false, out colors);
            }

            if (!allowDefault || !target.IsGeometryValid)
            {
                return false;
            }

            colors = new Color32[target.SourceMesh.vertexCount];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = VertexPaintChannelUtility.DefaultColor;
            }

            return true;
        }

        private void DrawScenePreview(IReadOnlyList<TargetDescriptor> targets)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            VertexPainterSettings settings = VertexPainterSettings.instance;
            if (!settings.ScenePreviewEnabled)
            {
                return;
            }

            byte defaultChannelValue = VertexPaintChannelUtility.GetByte(VertexPaintChannelUtility.DefaultColor, activeChannel);
            Material previewMaterial = GetScenePreviewMaterial();
            if (previewMaterial == null)
            {
                return;
            }

            previewMaterial.SetPass(0);

            foreach (TargetDescriptor target in targets)
            {
                if (!TryGetVisibleOrCurrentColorBuffer(target, true, out Color32[] colors))
                {
                    continue;
                }

                Vector3[] vertices = target.SourceMesh.vertices;
                if (vertices == null || vertices.Length == 0 || colors.Length != vertices.Length)
                {
                    continue;
                }

                ScenePreviewCacheEntry cacheEntry = GetOrCreateScenePreviewEntry(target);
                UpdateScenePreviewMesh(cacheEntry, target, colors, defaultChannelValue, settings.ScenePreviewOpacity);
                Graphics.DrawMeshNow(cacheEntry.Mesh, target.Renderer.localToWorldMatrix);
            }
        }

        private ScenePreviewCacheEntry GetOrCreateScenePreviewEntry(TargetDescriptor target)
        {
            int key = target.GameObject.GetInstanceID();
            int sourceMeshId = target.SourceMesh.GetInstanceID();
            if (scenePreviewEntries.TryGetValue(key, out ScenePreviewCacheEntry existingEntry) &&
                existingEntry != null &&
                existingEntry.Mesh != null &&
                existingEntry.SourceMeshId == sourceMeshId)
            {
                return existingEntry;
            }

            if (existingEntry != null && existingEntry.Mesh != null)
            {
                DestroyImmediate(existingEntry.Mesh);
            }

            Mesh previewMesh = Instantiate(target.SourceMesh);
            previewMesh.name = $"{target.GameObject.name}_VertexPaintPreview";
            previewMesh.hideFlags = HideFlags.HideAndDontSave;

            ScenePreviewCacheEntry newEntry = new()
            {
                SourceMeshId = sourceMeshId,
                Mesh = previewMesh
            };

            scenePreviewEntries[key] = newEntry;
            return newEntry;
        }

        private void UpdateScenePreviewMesh(
            ScenePreviewCacheEntry cacheEntry,
            TargetDescriptor target,
            Color32[] sourceColors,
            byte defaultChannelValue,
            float previewOpacity)
        {
            if (cacheEntry.Colors == null || cacheEntry.Colors.Length != sourceColors.Length)
            {
                cacheEntry.Colors = new Color32[sourceColors.Length];
            }

            for (int i = 0; i < sourceColors.Length; i++)
            {
                byte value = VertexPaintChannelUtility.GetByte(sourceColors[i], activeChannel);
                float factor = Mathf.Abs(value - defaultChannelValue) / 255f;
                cacheEntry.Colors[i] = ToPreviewColor32(factor, previewOpacity);
            }

            cacheEntry.Mesh.colors32 = cacheEntry.Colors;
            cacheEntry.Mesh.bounds = target.SourceMesh.bounds;
        }

        private Color32 ToPreviewColor32(float factor, float previewOpacity)
        {
            factor = Mathf.Clamp01(factor);
            float alpha = Mathf.Lerp(0f, previewOpacity, factor);
            Color color = activeChannel switch
            {
                VertexPaintChannel.Red => new Color(Mathf.Lerp(0.2f, 1f, factor), 0.12f, 0.12f, alpha),
                VertexPaintChannel.Green => new Color(0.12f, Mathf.Lerp(0.2f, 1f, factor), 0.12f, alpha),
                VertexPaintChannel.Blue => new Color(0.15f, 0.25f, Mathf.Lerp(0.25f, 1f, factor), alpha),
                VertexPaintChannel.Alpha => new Color(factor, factor, factor, alpha),
                _ => new Color(factor, factor, factor, alpha)
            };

            return (Color32)color;
        }

        private static Material GetScenePreviewMaterial()
        {
            if (scenePreviewMaterial != null)
            {
                return scenePreviewMaterial;
            }

            Shader shader = Shader.Find("Hidden/JuiceAI/VertexPainter/Preview");
            if (shader == null)
            {
                return null;
            }

            scenePreviewMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            return scenePreviewMaterial;
        }

        private void ClearScenePreviewEntries()
        {
            foreach (ScenePreviewCacheEntry entry in scenePreviewEntries.Values)
            {
                if (entry?.Mesh != null)
                {
                    DestroyImmediate(entry.Mesh);
                }
            }

            scenePreviewEntries.Clear();
        }

        private static int GetSampledVertexIndex(Mesh mesh, int triangleIndex, Vector3 barycentric)
        {
            int[] triangles = mesh.triangles;
            int start = triangleIndex * 3;
            if (triangles == null || start < 0 || start + 2 >= triangles.Length)
            {
                return -1;
            }

            if (barycentric.x >= barycentric.y && barycentric.x >= barycentric.z)
            {
                return triangles[start];
            }

            if (barycentric.y >= barycentric.z)
            {
                return triangles[start + 1];
            }

            return triangles[start + 2];
        }

        private void ShowActionSummary(string actionName, ActionReport report)
        {
            List<string> fragments = new();
            if (report.Applied > 0)
            {
                fragments.Add($"{report.Applied} updated");
            }

            if (report.AlreadyThere > 0)
            {
                fragments.Add($"{report.AlreadyThere} already there");
            }

            if (report.Unsupported > 0)
            {
                fragments.Add($"{report.Unsupported} unsupported");
            }

            if (report.MissingSource > 0)
            {
                fragments.Add($"{report.MissingSource} missing source paint");
            }

            if (report.Warnings > 0)
            {
                fragments.Add($"{report.Warnings} warning(s)");
            }

            string message = fragments.Count > 0
                ? $"{actionName}: {string.Join(", ", fragments)}."
                : $"{actionName}: nothing to do.";

            ShowNotification(new GUIContent(message));
        }
    }
}

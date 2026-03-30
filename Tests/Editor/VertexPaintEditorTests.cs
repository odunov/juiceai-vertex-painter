using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using JuiceAI.VertexPainter.Editor;

namespace JuiceAI.VertexPainter.Tests.Editor
{
    public class VertexPaintEditorTests
    {
        private const string TempRootFolder = "Assets/VertexPainterTests/Temp";

        [Test]
        public void Settings_Validation_BlocksPathsInsideModuleFolder()
        {
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                Assert.That(VertexPaintEditorUtility.TryGetModuleRootPath(out string moduleRoot), Is.True);

                settings.GeneratedDataRoot = "Assets/VertexPainter";
                Assert.That(settings.TryValidateGeneratedDataRoot(out _), Is.True);

                settings.GeneratedDataRoot = $"{moduleRoot}/Generated";
                Assert.That(settings.TryValidateGeneratedDataRoot(out string error), Is.False);
                if (moduleRoot.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    Assert.That(error, Does.Contain("cannot be written into the VertexPainter code folder"));
                }
                else
                {
                    Assert.That(error, Does.Contain("must be inside the project's Assets folder"));
                }
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
            }
        }

        [Test]
        [Ignore("Synced container mode disables scene-local paint authoring.")]
        public void CurrentPaintTarget_UsesSceneInstance_ForSceneSelections()
        {
            Mesh mesh = CreateTriangleMesh("SceneMesh");
            GameObject targetObject = new("ScenePaintTarget");
            targetObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            targetObject.AddComponent<MeshRenderer>();

            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                VertexPaintResolvedTarget currentPaintTarget = VertexPaintAuthoringContextUtility.ResolveCurrentPaintTarget(targetObject);

                Assert.That(currentPaintTarget.IsAvailable, Is.True);
                Assert.That(currentPaintTarget.Level, Is.EqualTo(VertexPaintOwnershipLevel.SceneInstance));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(targetObject);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void ContainerAndFoundationResolution_ResolveDistinctAssets_ForNestedPrefabInstance()
        {
            string tempRoot = CreateTempFolder();

            try
            {
                string meshPath = $"{tempRoot}/TestMesh.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";
                string roomBasePrefabPath = $"{tempRoot}/RoomBase.prefab";
                string roomVariantPrefabPath = $"{tempRoot}/RoomVariant.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset);

                GameObject roomBaseRoot = new("RoomBase");
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                moduleInstance.transform.SetParent(roomBaseRoot.transform, false);
                PrefabUtility.SaveAsPrefabAsset(roomBaseRoot, roomBasePrefabPath);
                UnityEngine.Object.DestroyImmediate(roomBaseRoot);

                GameObject roomBaseInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(roomBasePrefabPath));
                PrefabUtility.SaveAsPrefabAsset(roomBaseInstance, roomVariantPrefabPath);
                UnityEngine.Object.DestroyImmediate(roomBaseInstance);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject roomVariantInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(roomVariantPrefabPath));
                GameObject selectedRendererObject = roomVariantInstance.transform.Find("ModuleRenderer").gameObject;

                VertexPaintResolvedTarget sceneTarget = VertexPaintAuthoringContextUtility.Evaluate(selectedRendererObject, VertexPaintOwnershipLevel.SceneInstance);
                VertexPaintResolvedTarget containerTarget = VertexPaintAuthoringContextUtility.Evaluate(selectedRendererObject, VertexPaintOwnershipLevel.ContainerPrefab);
                VertexPaintResolvedTarget foundationTarget = VertexPaintAuthoringContextUtility.Evaluate(selectedRendererObject, VertexPaintOwnershipLevel.FoundationPrefab);

                Assert.That(sceneTarget.IsAvailable, Is.True);
                Assert.That(containerTarget.IsAvailable, Is.True);
                Assert.That(foundationTarget.IsAvailable, Is.True);
                Assert.That(containerTarget.ScopeKey, Is.EqualTo(VertexPaintEditorUtility.NormalizeAssetPath(roomVariantPrefabPath)));
                Assert.That(foundationTarget.ScopeKey, Is.EqualTo(VertexPaintEditorUtility.NormalizeAssetPath(modulePrefabPath)));

                UnityEngine.Object.DestroyImmediate(roomVariantInstance);
            }
            finally
            {
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void ContainerResolution_UsesSharedPrefab_ForDirectPrefabInstance()
        {
            string tempRoot = CreateTempFolder();

            try
            {
                string meshPath = $"{tempRoot}/TestMesh.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));

                VertexPaintResolvedTarget containerTarget = VertexPaintAuthoringContextUtility.Evaluate(moduleInstance, VertexPaintOwnershipLevel.ContainerPrefab);
                VertexPaintResolvedTarget foundationTarget = VertexPaintAuthoringContextUtility.Evaluate(moduleInstance, VertexPaintOwnershipLevel.FoundationPrefab);

                Assert.That(containerTarget.IsAvailable, Is.True);
                Assert.That(containerTarget.ScopeKey, Is.EqualTo(VertexPaintEditorUtility.NormalizeAssetPath(modulePrefabPath)));
                Assert.That(containerTarget.RelativePath, Is.EqualTo(string.Empty));
                Assert.That(foundationTarget.IsAvailable, Is.True);
                Assert.That(foundationTarget.ScopeKey, Is.EqualTo(VertexPaintEditorUtility.NormalizeAssetPath(modulePrefabPath)));

                UnityEngine.Object.DestroyImmediate(moduleInstance);
            }
            finally
            {
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void CurrentPaintTarget_UsesContainerPrefab_ForNestedSceneSelection()
        {
            string tempRoot = CreateTempFolder();

            try
            {
                string meshPath = $"{tempRoot}/TestMesh.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";
                string roomPrefabPath = $"{tempRoot}/Room.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset);

                GameObject roomRoot = new("Room");
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                moduleInstance.transform.SetParent(roomRoot.transform, false);
                PrefabUtility.SaveAsPrefabAsset(roomRoot, roomPrefabPath);
                UnityEngine.Object.DestroyImmediate(roomRoot);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject roomInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(roomPrefabPath));
                GameObject selectedRendererObject = roomInstance.transform.Find("ModuleRenderer").gameObject;

                VertexPaintResolvedTarget sceneTarget = VertexPaintAuthoringContextUtility.Evaluate(selectedRendererObject, VertexPaintOwnershipLevel.SceneInstance);
                VertexPaintResolvedTarget containerTarget = VertexPaintAuthoringContextUtility.Evaluate(selectedRendererObject, VertexPaintOwnershipLevel.ContainerPrefab);
                VertexPaintResolvedTarget foundationTarget = VertexPaintAuthoringContextUtility.Evaluate(selectedRendererObject, VertexPaintOwnershipLevel.FoundationPrefab);
                VertexPaintResolvedTarget currentPaintTarget = VertexPaintAuthoringContextUtility.ResolveCurrentPaintTarget(
                    selectedRendererObject,
                    sceneTarget,
                    containerTarget,
                    foundationTarget);

                Assert.That(currentPaintTarget.IsAvailable, Is.True);
                Assert.That(currentPaintTarget.Level, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));
                Assert.That(currentPaintTarget.ScopeKey, Is.EqualTo(VertexPaintEditorUtility.NormalizeAssetPath(roomPrefabPath)));

                UnityEngine.Object.DestroyImmediate(roomInstance);
            }
            finally
            {
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void CurrentPaintTarget_UsesSharedPrefab_ForDirectScenePrefabInstance()
        {
            string tempRoot = CreateTempFolder();

            try
            {
                string meshPath = $"{tempRoot}/TestMesh.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));

                VertexPaintResolvedTarget currentPaintTarget = VertexPaintAuthoringContextUtility.ResolveCurrentPaintTarget(moduleInstance);

                Assert.That(currentPaintTarget.IsAvailable, Is.True);
                Assert.That(currentPaintTarget.Level, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));
                Assert.That(currentPaintTarget.ScopeKey, Is.EqualTo(VertexPaintEditorUtility.NormalizeAssetPath(modulePrefabPath)));

                UnityEngine.Object.DestroyImmediate(moduleInstance);
            }
            finally
            {
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void SharedContainerPaint_RoundTripsBetweenSceneAndPrefab_ForDirectPrefabInstance()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                string meshPath = $"{tempRoot}/TestMesh.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                VertexPainterWindow.TargetDescriptor sceneTarget = CreateSceneTargetDescriptor(moduleInstance, moduleInstance);
                Color32[] sceneColors = CreateColorBuffer(meshAsset.vertexCount, new Color32(180, 40, 40, 255));

                using (VertexPaintEditorUtility.SharedContainerPaintSession sceneSession = new("Paint Direct Prefab From Scene"))
                {
                    Assert.That(sceneSession.TryGetEditableColors(sceneTarget, out Color32[] editableColors, out string error), Is.True, error);
                    Array.Copy(sceneColors, editableColors, sceneColors.Length);
                    sceneSession.MarkDirty(sceneTarget);
                    Assert.That(sceneSession.Commit(out string commitError), Is.True, commitError);
                }

                GameObject loadedModuleRoot = PrefabUtility.LoadPrefabContents(modulePrefabPath);
                try
                {
                    VertexPaintBinding binding = loadedModuleRoot.GetComponent<VertexPaintBinding>();
                    Assert.That(binding, Is.Not.Null);
                    Assert.That(binding.PaintAsset, Is.Not.Null);
                    Assert.That(binding.PaintAsset.GetColorsCopy(), Is.EqualTo(sceneColors));

                    VertexPainterWindow.TargetDescriptor liveTarget = CreateLiveContainerTargetDescriptor(
                        loadedModuleRoot,
                        modulePrefabPath,
                        AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));

                    Color32[] prefabColors = CreateColorBuffer(meshAsset.vertexCount, new Color32(30, 150, 220, 255));
                    Assert.That(
                        VertexPaintEditorUtility.TryCopyColorBufferToTarget(
                            liveTarget,
                            liveTarget.ContainerTarget,
                            prefabColors,
                            "Paint Direct Prefab",
                            out string liveError),
                        Is.True,
                        liveError);

                    PrefabUtility.SaveAsPrefabAsset(loadedModuleRoot, modulePrefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(loadedModuleRoot);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(modulePrefabPath, ImportAssetOptions.ForceUpdate);
                VertexPaintEditorUtility.RefreshLoadedBindings();

                VertexPainterWindow.TargetDescriptor refreshedSceneTarget = CreateSceneTargetDescriptor(moduleInstance, moduleInstance);
                Assert.That(refreshedSceneTarget.VisiblePaintOwner, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));
                Assert.That(
                    refreshedSceneTarget.ContainerTarget.PaintAsset.GetColorsCopy(),
                    Is.EqualTo(CreateColorBuffer(meshAsset.vertexCount, new Color32(30, 150, 220, 255))));

                UnityEngine.Object.DestroyImmediate(moduleInstance);
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void ContainerResolution_UsesOutermostRoot_ForThreeLevelNestedPrefabSelection()
        {
            string tempRoot = CreateTempFolder();

            try
            {
                CreateThreeLevelNestedPrefabs(
                    tempRoot,
                    out _,
                    out string leafPrefabPath,
                    out string midPrefabPath,
                    out string outerPrefabPath);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject outerInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(outerPrefabPath));
                GameObject selectedLeafObject = outerInstance.transform.Find("Mid/Leaf").gameObject;

                Assert.That(VertexPaintEditorUtility.ResolveSelectedAuthoringRoot(selectedLeafObject), Is.SameAs(outerInstance));
                Assert.That(VertexPaintEditorUtility.ResolveSelectedAuthoringRoot(outerInstance.transform.Find("Mid").gameObject), Is.SameAs(outerInstance));

                VertexPainterWindow.TargetDescriptor rootSelectionTarget = CreateSceneTargetDescriptor(selectedLeafObject, outerInstance);
                VertexPainterWindow.TargetDescriptor childSelectionTarget = CreateSceneTargetDescriptor(selectedLeafObject, selectedLeafObject);

                Assert.That(rootSelectionTarget.ContainerTarget.ScopeKey, Is.EqualTo(VertexPaintEditorUtility.NormalizeAssetPath(outerPrefabPath)));
                Assert.That(rootSelectionTarget.ContainerTarget.RelativePath, Is.EqualTo("Mid[0]/Leaf[0]"));
                Assert.That(rootSelectionTarget.FoundationTarget.ScopeKey, Is.EqualTo(VertexPaintEditorUtility.NormalizeAssetPath(leafPrefabPath)));

                Assert.That(childSelectionTarget.AuthoringRoot, Is.SameAs(outerInstance));
                Assert.That(childSelectionTarget.ContainerTarget.ScopeKey, Is.EqualTo(rootSelectionTarget.ContainerTarget.ScopeKey));
                Assert.That(childSelectionTarget.ContainerTarget.RelativePath, Is.EqualTo(rootSelectionTarget.ContainerTarget.RelativePath));

                GameObject loadedOuterRoot = PrefabUtility.LoadPrefabContents(outerPrefabPath);
                try
                {
                    GameObject loadedLeafObject = loadedOuterRoot.transform.Find("Mid/Leaf").gameObject;
                    VertexPainterWindow.TargetDescriptor liveTarget = CreateLiveContainerTargetDescriptor(
                        loadedLeafObject,
                        outerPrefabPath,
                        AssetDatabase.LoadAssetAtPath<GameObject>(leafPrefabPath));

                    Assert.That(liveTarget.ContainerTarget.ScopeKey, Is.EqualTo(rootSelectionTarget.ContainerTarget.ScopeKey));
                    Assert.That(liveTarget.ContainerTarget.RelativePath, Is.EqualTo(rootSelectionTarget.ContainerTarget.RelativePath));
                    Assert.That(liveTarget.FoundationTarget.ScopeKey, Is.EqualTo(VertexPaintEditorUtility.NormalizeAssetPath(leafPrefabPath)));
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(loadedOuterRoot);
                }

                UnityEngine.Object.DestroyImmediate(outerInstance);
            }
            finally
            {
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void SharedContainerPaintSession_CommitsSharedContainerPaint_WithoutSceneOwnership()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                string meshPath = $"{tempRoot}/TestMesh.asset";
                string foundationPaintPath = $"{tempRoot}/FoundationPaint.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";
                string roomPrefabPath = $"{tempRoot}/Room.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                VertexPaintAsset foundationPaint = CreatePaintAsset(foundationPaintPath, meshAsset);
                foundationPaint.OverwriteColors(CreateColorBuffer(meshAsset.vertexCount, new Color32(10, 20, 30, 255)));
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset, foundationPaint);

                GameObject roomRoot = new("Room");
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                moduleInstance.transform.SetParent(roomRoot.transform, false);
                PrefabUtility.SaveAsPrefabAsset(roomRoot, roomPrefabPath);
                UnityEngine.Object.DestroyImmediate(roomRoot);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject roomInstanceA = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(roomPrefabPath));
                GameObject roomInstanceB = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(roomPrefabPath));
                GameObject selectedRendererA = roomInstanceA.transform.Find("ModuleRenderer").gameObject;
                GameObject selectedRendererB = roomInstanceB.transform.Find("ModuleRenderer").gameObject;

                VertexPainterWindow.TargetDescriptor target = CreateSceneTargetDescriptor(selectedRendererA);
                Color32[] expectedColors = CreateColorBuffer(meshAsset.vertexCount, new Color32(160, 90, 45, 255));

                using VertexPaintEditorUtility.SharedContainerPaintSession session = new("Synced Container Paint");
                Assert.That(session.TryGetEditableColors(target, out Color32[] editableColors, out string error), Is.True, error);
                Array.Copy(expectedColors, editableColors, expectedColors.Length);
                session.MarkDirty(target);
                Assert.That(session.Commit(out string commitError), Is.True, commitError);

                VertexPainterWindow.TargetDescriptor refreshedA = CreateSceneTargetDescriptor(selectedRendererA);
                VertexPainterWindow.TargetDescriptor refreshedB = CreateSceneTargetDescriptor(selectedRendererB);

                Assert.That(refreshedA.SceneTarget.OwnsPaint, Is.False);
                Assert.That(refreshedA.CurrentPaintTarget.Level, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));
                Assert.That(refreshedA.VisiblePaintOwner, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));
                Assert.That(refreshedA.ContainerTarget.PaintAsset, Is.Not.Null);
                Assert.That(refreshedA.ContainerTarget.PaintAsset.GetColorsCopy(), Is.EqualTo(expectedColors));
                Assert.That(refreshedB.VisiblePaintOwner, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));
                Assert.That(refreshedB.ContainerTarget.PaintAsset, Is.Not.Null);
                Assert.That(refreshedB.ContainerTarget.PaintAsset.GetColorsCopy(), Is.EqualTo(expectedColors));

                UnityEngine.Object.DestroyImmediate(roomInstanceA);
                UnityEngine.Object.DestroyImmediate(roomInstanceB);
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void SharedContainerPaintSession_ClearResetsContainerToFoundationPaint()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                string meshPath = $"{tempRoot}/TestMesh.asset";
                string foundationPaintPath = $"{tempRoot}/FoundationPaint.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";
                string roomPrefabPath = $"{tempRoot}/Room.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                VertexPaintAsset foundationPaint = CreatePaintAsset(foundationPaintPath, meshAsset);
                foundationPaint.OverwriteColors(CreateColorBuffer(meshAsset.vertexCount, new Color32(35, 70, 105, 255)));
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset, foundationPaint);

                GameObject roomRoot = new("Room");
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                moduleInstance.transform.SetParent(roomRoot.transform, false);
                PrefabUtility.SaveAsPrefabAsset(roomRoot, roomPrefabPath);
                UnityEngine.Object.DestroyImmediate(roomRoot);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject roomInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(roomPrefabPath));
                GameObject selectedRendererObject = roomInstance.transform.Find("ModuleRenderer").gameObject;
                VertexPainterWindow.TargetDescriptor target = CreateSceneTargetDescriptor(selectedRendererObject);

                using (VertexPaintEditorUtility.SharedContainerPaintSession paintSession = new("Create Synced Container Paint"))
                {
                    Assert.That(paintSession.TryGetEditableColors(target, out Color32[] editableColors, out string paintError), Is.True, paintError);
                    Color32[] changedColors = CreateColorBuffer(meshAsset.vertexCount, new Color32(190, 30, 30, 255));
                    Array.Copy(changedColors, editableColors, changedColors.Length);
                    paintSession.MarkDirty(target);
                    Assert.That(paintSession.Commit(out string commitError), Is.True, commitError);
                }

                VertexPainterWindow.TargetDescriptor paintedTarget = CreateSceneTargetDescriptor(selectedRendererObject);
                Assert.That(paintedTarget.VisiblePaintOwner, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));

                using (VertexPaintEditorUtility.SharedContainerPaintSession clearSession = new("Clear Synced Container Paint"))
                {
                    Assert.That(clearSession.TryClearToInherited(paintedTarget, out string clearError), Is.True, clearError);
                    Assert.That(clearSession.Commit(out string commitError), Is.True, commitError);
                }

                VertexPainterWindow.TargetDescriptor clearedTarget = CreateSceneTargetDescriptor(selectedRendererObject);
                Assert.That(clearedTarget.ContainerTarget.OwnsPaint, Is.False);
                Assert.That(clearedTarget.VisiblePaintOwner, Is.EqualTo(VertexPaintOwnershipLevel.FoundationPrefab));
                Assert.That(clearedTarget.ContainerTarget.PaintAsset, Is.SameAs(foundationPaint));

                UnityEngine.Object.DestroyImmediate(roomInstance);
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void SharedContainerPaintSession_CreatesContainerBinding_WhenFoundationHasNoBinding()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                string meshPath = $"{tempRoot}/TestMesh.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";
                string roomPrefabPath = $"{tempRoot}/Room.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset);

                GameObject roomRoot = new("Room");
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                moduleInstance.transform.SetParent(roomRoot.transform, false);
                PrefabUtility.SaveAsPrefabAsset(roomRoot, roomPrefabPath);
                UnityEngine.Object.DestroyImmediate(roomRoot);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject roomInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(roomPrefabPath));
                GameObject selectedRendererObject = roomInstance.transform.Find("ModuleRenderer").gameObject;
                VertexPainterWindow.TargetDescriptor target = CreateSceneTargetDescriptor(selectedRendererObject);

                using VertexPaintEditorUtility.SharedContainerPaintSession session = new("Create Shared Container Binding");
                Assert.That(session.TryGetEditableColors(target, out Color32[] editableColors, out string error), Is.True, error);

                Color32[] expectedColors = CreateColorBuffer(meshAsset.vertexCount, new Color32(90, 140, 190, 255));
                Array.Copy(expectedColors, editableColors, expectedColors.Length);
                session.MarkDirty(target);
                Assert.That(session.Commit(out string commitError), Is.True, commitError);

                GameObject loadedRoomRoot = PrefabUtility.LoadPrefabContents(roomPrefabPath);
                try
                {
                    GameObject loadedRendererObject = loadedRoomRoot.transform.Find("ModuleRenderer").gameObject;
                    VertexPaintBinding containerBinding = loadedRendererObject.GetComponent<VertexPaintBinding>();
                    Assert.That(containerBinding, Is.Not.Null);
                    Assert.That(containerBinding.PaintAsset, Is.Not.Null);
                    Assert.That(containerBinding.PaintAsset.GetColorsCopy(), Is.EqualTo(expectedColors));
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(loadedRoomRoot);
                }

                VertexPainterWindow.TargetDescriptor refreshedTarget = CreateSceneTargetDescriptor(selectedRendererObject);
                Assert.That(refreshedTarget.SceneTarget.OwnsPaint, Is.False);
                Assert.That(refreshedTarget.VisiblePaintOwner, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));
                Assert.That(refreshedTarget.CurrentPaintTarget.Level, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));

                UnityEngine.Object.DestroyImmediate(roomInstance);
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void SharedContainerPaintSession_ResolvesDuplicateSiblingPaths_Independently()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                string meshPath = $"{tempRoot}/TestMesh.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";
                string roomPrefabPath = $"{tempRoot}/Room.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset);

                GameObject roomRoot = new("Room");
                GameObject firstModuleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                firstModuleInstance.transform.SetParent(roomRoot.transform, false);
                GameObject secondModuleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                secondModuleInstance.transform.SetParent(roomRoot.transform, false);
                PrefabUtility.SaveAsPrefabAsset(roomRoot, roomPrefabPath);
                UnityEngine.Object.DestroyImmediate(roomRoot);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject roomInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(roomPrefabPath));
                GameObject selectedRendererA = roomInstance.transform.GetChild(0).gameObject;
                GameObject selectedRendererB = roomInstance.transform.GetChild(1).gameObject;

                VertexPainterWindow.TargetDescriptor targetA = CreateSceneTargetDescriptor(selectedRendererA);
                VertexPainterWindow.TargetDescriptor targetB = CreateSceneTargetDescriptor(selectedRendererB);

                Assert.That(targetA.CurrentPaintTarget.Level, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));
                Assert.That(targetB.CurrentPaintTarget.Level, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));
                Assert.That(targetA.CurrentPaintTarget.RelativePath, Is.EqualTo("ModuleRenderer[0]"));
                Assert.That(targetB.CurrentPaintTarget.RelativePath, Is.EqualTo("ModuleRenderer[1]"));

                Color32[] colorsA = CreateColorBuffer(meshAsset.vertexCount, new Color32(220, 30, 30, 255));
                Color32[] colorsB = CreateColorBuffer(meshAsset.vertexCount, new Color32(30, 80, 220, 255));

                using (VertexPaintEditorUtility.SharedContainerPaintSession session = new("Paint Duplicate Nested Prefabs"))
                {
                    Assert.That(session.TryGetEditableColors(targetA, out Color32[] editableColorsA, out string errorA), Is.True, errorA);
                    Array.Copy(colorsA, editableColorsA, colorsA.Length);
                    session.MarkDirty(targetA);

                    Assert.That(session.TryGetEditableColors(targetB, out Color32[] editableColorsB, out string errorB), Is.True, errorB);
                    Array.Copy(colorsB, editableColorsB, colorsB.Length);
                    session.MarkDirty(targetB);

                    Assert.That(session.Commit(out string commitError), Is.True, commitError);
                }

                GameObject loadedRoomRoot = PrefabUtility.LoadPrefabContents(roomPrefabPath);
                try
                {
                    VertexPaintBinding firstBinding = loadedRoomRoot.transform.GetChild(0).GetComponent<VertexPaintBinding>();
                    VertexPaintBinding secondBinding = loadedRoomRoot.transform.GetChild(1).GetComponent<VertexPaintBinding>();

                    Assert.That(firstBinding, Is.Not.Null);
                    Assert.That(secondBinding, Is.Not.Null);
                    Assert.That(firstBinding.PaintAsset, Is.Not.Null);
                    Assert.That(secondBinding.PaintAsset, Is.Not.Null);
                    Assert.That(firstBinding.PaintAsset.GetColorsCopy(), Is.EqualTo(colorsA));
                    Assert.That(secondBinding.PaintAsset.GetColorsCopy(), Is.EqualTo(colorsB));
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(loadedRoomRoot);
                }

                UnityEngine.Object.DestroyImmediate(roomInstance);
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void SharedContainerPaint_RoundTripsBetweenSceneAndLiveOuterPrefab_ForThreeLevelNestedPrefab()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                CreateThreeLevelNestedPrefabs(
                    tempRoot,
                    out Mesh meshAsset,
                    out string leafPrefabPath,
                    out _,
                    out string outerPrefabPath);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject outerInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(outerPrefabPath));
                GameObject selectedLeafObject = outerInstance.transform.Find("Mid/Leaf").gameObject;

                VertexPainterWindow.TargetDescriptor sceneTarget = CreateSceneTargetDescriptor(selectedLeafObject, outerInstance);
                Color32[] sceneColors = CreateColorBuffer(meshAsset.vertexCount, new Color32(170, 60, 25, 255));

                using (VertexPaintEditorUtility.SharedContainerPaintSession sceneSession = new("Paint Outer From Scene"))
                {
                    Assert.That(sceneSession.TryGetEditableColors(sceneTarget, out Color32[] editableColors, out string error), Is.True, error);
                    Array.Copy(sceneColors, editableColors, sceneColors.Length);
                    sceneSession.MarkDirty(sceneTarget);
                    Assert.That(sceneSession.Commit(out string commitError), Is.True, commitError);
                }

                VertexPainterWindow.TargetDescriptor refreshedSceneTarget = CreateSceneTargetDescriptor(selectedLeafObject, outerInstance);
                string sharedPaintPath = AssetDatabase.GetAssetPath(refreshedSceneTarget.ContainerTarget.PaintAsset);

                Assert.That(refreshedSceneTarget.VisiblePaintOwner, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));
                Assert.That(refreshedSceneTarget.ContainerTarget.PaintAsset, Is.Not.Null);
                Assert.That(refreshedSceneTarget.ContainerTarget.PaintAsset.GetColorsCopy(), Is.EqualTo(sceneColors));

                GameObject loadedOuterRoot = PrefabUtility.LoadPrefabContents(outerPrefabPath);
                try
                {
                    GameObject loadedLeafObject = loadedOuterRoot.transform.Find("Mid/Leaf").gameObject;
                    VertexPainterWindow.TargetDescriptor liveTarget = CreateLiveContainerTargetDescriptor(
                        loadedLeafObject,
                        outerPrefabPath,
                        AssetDatabase.LoadAssetAtPath<GameObject>(leafPrefabPath));

                    Assert.That(AssetDatabase.GetAssetPath(liveTarget.ContainerTarget.PaintAsset), Is.EqualTo(sharedPaintPath));

                    Color32[] prefabColors = CreateColorBuffer(meshAsset.vertexCount, new Color32(35, 120, 210, 255));
                    Assert.That(
                        VertexPaintEditorUtility.TryCopyColorBufferToTarget(
                            liveTarget,
                            liveTarget.ContainerTarget,
                            prefabColors,
                            "Paint Outer From Prefab",
                            out string liveError),
                        Is.True,
                        liveError);

                    PrefabUtility.SaveAsPrefabAsset(loadedOuterRoot, outerPrefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(loadedOuterRoot);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(outerPrefabPath, ImportAssetOptions.ForceUpdate);
                VertexPaintEditorUtility.RefreshLoadedBindings();

                VertexPainterWindow.TargetDescriptor roundTrippedSceneTarget = CreateSceneTargetDescriptor(selectedLeafObject, outerInstance);
                Assert.That(AssetDatabase.GetAssetPath(roundTrippedSceneTarget.ContainerTarget.PaintAsset), Is.EqualTo(sharedPaintPath));
                Assert.That(
                    roundTrippedSceneTarget.ContainerTarget.PaintAsset.GetColorsCopy(),
                    Is.EqualTo(CreateColorBuffer(meshAsset.vertexCount, new Color32(35, 120, 210, 255))));

                UnityEngine.Object.DestroyImmediate(outerInstance);
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void SharedContainerPaintSession_IgnoresLegacyIntermediateContainerPaint_ForOutermostOwnership()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                CreateThreeLevelNestedPrefabs(
                    tempRoot,
                    out Mesh meshAsset,
                    out string leafPrefabPath,
                    out string midPrefabPath,
                    out string outerPrefabPath);

                Color32[] legacyMidColors = CreateColorBuffer(meshAsset.vertexCount, new Color32(210, 35, 70, 255));
                GameObject loadedMidRoot = PrefabUtility.LoadPrefabContents(midPrefabPath);
                try
                {
                    GameObject loadedLeafObject = loadedMidRoot.transform.Find("Leaf").gameObject;
                    VertexPainterWindow.TargetDescriptor legacyMidTarget = CreateLiveContainerTargetDescriptor(
                        loadedLeafObject,
                        midPrefabPath,
                        AssetDatabase.LoadAssetAtPath<GameObject>(leafPrefabPath));

                    Assert.That(
                        VertexPaintEditorUtility.TryCopyColorBufferToTarget(
                            legacyMidTarget,
                            legacyMidTarget.ContainerTarget,
                            legacyMidColors,
                            "Create Legacy Mid Paint",
                            out string legacyError),
                        Is.True,
                        legacyError);

                    PrefabUtility.SaveAsPrefabAsset(loadedMidRoot, midPrefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(loadedMidRoot);
                }

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject outerInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(outerPrefabPath));
                GameObject selectedLeafObject = outerInstance.transform.Find("Mid/Leaf").gameObject;
                VertexPainterWindow.TargetDescriptor outerTarget = CreateSceneTargetDescriptor(selectedLeafObject, outerInstance);

                Assert.That(outerTarget.ContainerTarget.ScopeKey, Is.EqualTo(VertexPaintEditorUtility.NormalizeAssetPath(outerPrefabPath)));
                Assert.That(outerTarget.ContainerTarget.HasPaint, Is.True);
                Assert.That(outerTarget.ContainerTarget.OwnsPaint, Is.False);
                Assert.That(outerTarget.VisiblePaintOwner, Is.Null);

                using (VertexPaintEditorUtility.SharedContainerPaintSession session = new("Create Outer Root Paint"))
                {
                    Assert.That(session.TryGetEditableColors(outerTarget, out Color32[] editableColors, out string error), Is.True, error);
                    Assert.That(
                        editableColors,
                        Is.EqualTo(CreateColorBuffer(meshAsset.vertexCount, VertexPaintChannelUtility.DefaultColor)));

                    Color32[] outerColors = CreateColorBuffer(meshAsset.vertexCount, new Color32(45, 160, 45, 255));
                    Array.Copy(outerColors, editableColors, outerColors.Length);
                    session.MarkDirty(outerTarget);
                    Assert.That(session.Commit(out string commitError), Is.True, commitError);
                }

                VertexPainterWindow.TargetDescriptor refreshedOuterTarget = CreateSceneTargetDescriptor(selectedLeafObject, outerInstance);
                Assert.That(refreshedOuterTarget.VisiblePaintOwner, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));
                Assert.That(
                    refreshedOuterTarget.ContainerTarget.PaintAsset.GetColorsCopy(),
                    Is.EqualTo(CreateColorBuffer(meshAsset.vertexCount, new Color32(45, 160, 45, 255))));

                GameObject reloadedMidRoot = PrefabUtility.LoadPrefabContents(midPrefabPath);
                try
                {
                    VertexPaintBinding midBinding = reloadedMidRoot.transform.Find("Leaf").GetComponent<VertexPaintBinding>();
                    Assert.That(midBinding, Is.Not.Null);
                    Assert.That(midBinding.PaintAsset, Is.Not.Null);
                    Assert.That(midBinding.PaintAsset.GetColorsCopy(), Is.EqualTo(legacyMidColors));
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(reloadedMidRoot);
                }

                UnityEngine.Object.DestroyImmediate(outerInstance);
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        [Ignore("Synced container mode disables scene-local paint authoring.")]
        public void EnsureTargetPaintAsset_ForksInheritedPaint_ForSceneInstance()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                string meshPath = $"{tempRoot}/TestMesh.asset";
                string foundationPaintPath = $"{tempRoot}/FoundationPaint.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";
                string roomPrefabPath = $"{tempRoot}/Room.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                VertexPaintAsset foundationPaint = CreatePaintAsset(foundationPaintPath, meshAsset);
                foundationPaint.OverwriteColors(CreateColorBuffer(meshAsset.vertexCount, new Color32(12, 34, 56, 255)));
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset, foundationPaint);

                GameObject roomRoot = new("Room");
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                moduleInstance.transform.SetParent(roomRoot.transform, false);
                PrefabUtility.SaveAsPrefabAsset(roomRoot, roomPrefabPath);
                UnityEngine.Object.DestroyImmediate(roomRoot);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject roomInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(roomPrefabPath));
                GameObject selectedRendererObject = roomInstance.transform.Find("ModuleRenderer").gameObject;

                VertexPaintResolvedTarget sceneTarget = VertexPaintAuthoringContextUtility.Evaluate(selectedRendererObject, VertexPaintOwnershipLevel.SceneInstance);
                VertexPaintResolvedTarget containerTarget = VertexPaintAuthoringContextUtility.Evaluate(selectedRendererObject, VertexPaintOwnershipLevel.ContainerPrefab);
                VertexPaintResolvedTarget foundationTarget = VertexPaintAuthoringContextUtility.Evaluate(selectedRendererObject, VertexPaintOwnershipLevel.FoundationPrefab);
                sceneTarget = VertexPaintAuthoringContextUtility.NormalizeSceneTarget(sceneTarget, containerTarget, foundationTarget);
                VertexPaintResolvedTarget currentPaintTarget = VertexPaintAuthoringContextUtility.ResolveCurrentPaintTarget(
                    selectedRendererObject,
                    sceneTarget,
                    containerTarget,
                    foundationTarget);
                VertexPaintOwnershipLevel? visibleOwner = VertexPaintAuthoringContextUtility.GetVisiblePaintOwner(sceneTarget, containerTarget, foundationTarget);

                VertexPainterWindow.TargetDescriptor target = new(
                    selectedRendererObject,
                    VertexPaintEditorUtility.ResolveSelectedAuthoringRoot(selectedRendererObject),
                    selectedRendererObject.GetComponent<MeshFilter>(),
                    selectedRendererObject.GetComponent<MeshRenderer>(),
                    meshAsset,
                    sceneTarget,
                    containerTarget,
                    foundationTarget,
                    currentPaintTarget,
                    visibleOwner,
                    null);

                Assert.That(sceneTarget.OwnsPaint, Is.False);
                Assert.That(sceneTarget.PaintAsset, Is.Null);
                Assert.That(visibleOwner, Is.EqualTo(VertexPaintOwnershipLevel.FoundationPrefab));

                Assert.That(
                    VertexPaintEditorUtility.TryEnsureTargetPaintAsset(target, sceneTarget, "Fork Scene Paint", out VertexPaintAsset scenePaint, out string error),
                    Is.True,
                    error);

                Assert.That(scenePaint, Is.Not.Null);
                Assert.That(scenePaint, Is.Not.SameAs(foundationPaint));
                Assert.That(AssetDatabase.GetAssetPath(scenePaint), Does.Contain("/Generated/"));
                Assert.That(scenePaint.GetColorsCopy(), Is.EqualTo(foundationPaint.GetColorsCopy()));

                VertexPaintBinding binding = selectedRendererObject.GetComponent<VertexPaintBinding>();
                Assert.That(binding, Is.Not.Null);
                Assert.That(binding.PaintAsset, Is.SameAs(scenePaint));

                SerializedObject serializedBinding = new(binding);
                SerializedProperty paintAssetProperty = serializedBinding.FindProperty("paintAsset");
                Assert.That(paintAssetProperty, Is.Not.Null);
                Assert.That(paintAssetProperty.prefabOverride, Is.True);

                UnityEngine.Object.DestroyImmediate(roomInstance);
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        [Ignore("Synced container mode disables scene-local paint authoring.")]
        public void ClearScenePaint_DeletesOldAsset_AndNextEditStartsFromInheritedColors()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                string meshPath = $"{tempRoot}/TestMesh.asset";
                string foundationPaintPath = $"{tempRoot}/FoundationPaint.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";
                string roomPrefabPath = $"{tempRoot}/Room.prefab";
                string scenePath = $"{tempRoot}/IsolationScene.unity";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                VertexPaintAsset foundationPaint = CreatePaintAsset(foundationPaintPath, meshAsset);
                foundationPaint.OverwriteColors(CreateColorBuffer(meshAsset.vertexCount, new Color32(25, 50, 75, 255)));
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset, foundationPaint);

                GameObject roomRoot = new("Room");
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                moduleInstance.transform.SetParent(roomRoot.transform, false);
                PrefabUtility.SaveAsPrefabAsset(roomRoot, roomPrefabPath);
                UnityEngine.Object.DestroyImmediate(roomRoot);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), scenePath);

                GameObject roomInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(roomPrefabPath));
                GameObject selectedRendererObject = roomInstance.transform.Find("ModuleRenderer").gameObject;
                VertexPainterWindow.TargetDescriptor target = CreateSceneTargetDescriptor(selectedRendererObject);

                Assert.That(
                    VertexPaintEditorUtility.TryEnsureTargetPaintAsset(target, target.SceneTarget, "Create Scene Paint", out VertexPaintAsset scenePaint, out string createError),
                    Is.True,
                    createError);

                scenePaint.OverwriteColors(CreateColorBuffer(meshAsset.vertexCount, new Color32(200, 10, 10, 255)));
                VertexPaintEditorUtility.RebuildStreamMesh(scenePaint);
                string scenePaintPath = AssetDatabase.GetAssetPath(scenePaint);

                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), scenePath);

                Assert.That(
                    VertexPaintEditorUtility.TryClearTargetOwnership(target, target.SceneTarget, "Clear Scene Paint", out string clearError),
                    Is.True,
                    clearError);

                Assert.That(AssetDatabase.LoadAssetAtPath<VertexPaintAsset>(scenePaintPath), Is.Null);

                VertexPainterWindow.TargetDescriptor refreshedTarget = CreateSceneTargetDescriptor(selectedRendererObject);
                Assert.That(refreshedTarget.SceneTarget.OwnsPaint, Is.False);
                Assert.That(refreshedTarget.VisiblePaintOwner, Is.EqualTo(VertexPaintOwnershipLevel.FoundationPrefab));

                Assert.That(
                    VertexPaintEditorUtility.TryEnsureTargetPaintAsset(refreshedTarget, refreshedTarget.SceneTarget, "Recreate Scene Paint", out VertexPaintAsset recreatedScenePaint, out string recreateError),
                    Is.True,
                    recreateError);

                Assert.That(recreatedScenePaint, Is.Not.Null);
                Assert.That(recreatedScenePaint, Is.Not.SameAs(foundationPaint));
                Assert.That(recreatedScenePaint.GetColorsCopy(), Is.EqualTo(foundationPaint.GetColorsCopy()));

                UnityEngine.Object.DestroyImmediate(roomInstance);
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        [Ignore("Synced container mode disables scene-local paint authoring.")]
        public void ClearScenePaint_RemovesAddedBindingOverride_WhenSourceHasNoBinding()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                string meshPath = $"{tempRoot}/TestMesh.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                VertexPainterWindow.TargetDescriptor target = CreateSceneTargetDescriptor(moduleInstance);

                Assert.That(
                    VertexPaintEditorUtility.TryEnsureTargetPaintAsset(target, target.SceneTarget, "Create Scene Paint", out VertexPaintAsset scenePaint, out string createError),
                    Is.True,
                    createError);

                string scenePaintPath = AssetDatabase.GetAssetPath(scenePaint);
                VertexPaintBinding binding = moduleInstance.GetComponent<VertexPaintBinding>();
                Assert.That(binding, Is.Not.Null);
                Assert.That(PrefabUtility.IsAddedComponentOverride(binding), Is.True);

                Assert.That(
                    VertexPaintEditorUtility.TryClearTargetOwnership(target, target.SceneTarget, "Clear Scene Paint", out string clearError),
                    Is.True,
                    clearError);

                Assert.That(moduleInstance.GetComponent<VertexPaintBinding>(), Is.Null);
                Assert.That(AssetDatabase.LoadAssetAtPath<VertexPaintAsset>(scenePaintPath), Is.Null);

                VertexPainterWindow.TargetDescriptor refreshedTarget = CreateSceneTargetDescriptor(moduleInstance);
                Assert.That(refreshedTarget.SceneTarget.OwnsPaint, Is.False);
                Assert.That(refreshedTarget.VisiblePaintOwner, Is.Null);

                Assert.That(
                    VertexPaintEditorUtility.TryEnsureTargetPaintAsset(refreshedTarget, refreshedTarget.SceneTarget, "Recreate Scene Paint", out VertexPaintAsset recreatedScenePaint, out string recreateError),
                    Is.True,
                    recreateError);

                Assert.That(recreatedScenePaint, Is.Not.Null);
                Assert.That(recreatedScenePaint, Is.Not.SameAs(scenePaint));
                Assert.That(recreatedScenePaint.GetColorsCopy(), Is.EqualTo(CreateColorBuffer(meshAsset.vertexCount, VertexPaintChannelUtility.DefaultColor)));

                UnityEngine.Object.DestroyImmediate(moduleInstance);
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        [Ignore("Synced container mode removes push/pull authoring.")]
        public void CopyColorBufferToContainer_PersistsPushedSceneColors_AndPullRevealsThem()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                string meshPath = $"{tempRoot}/TestMesh.asset";
                string foundationPaintPath = $"{tempRoot}/FoundationPaint.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";
                string roomPrefabPath = $"{tempRoot}/Room.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                VertexPaintAsset foundationPaint = CreatePaintAsset(foundationPaintPath, meshAsset);
                foundationPaint.OverwriteColors(CreateColorBuffer(meshAsset.vertexCount, new Color32(20, 40, 60, 255)));
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset, foundationPaint);

                GameObject roomRoot = new("Room");
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                moduleInstance.transform.SetParent(roomRoot.transform, false);
                PrefabUtility.SaveAsPrefabAsset(roomRoot, roomPrefabPath);
                UnityEngine.Object.DestroyImmediate(roomRoot);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject roomInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(roomPrefabPath));
                GameObject selectedRendererObject = roomInstance.transform.Find("ModuleRenderer").gameObject;
                VertexPainterWindow.TargetDescriptor target = CreateSceneTargetDescriptor(selectedRendererObject);

                Assert.That(
                    VertexPaintEditorUtility.TryEnsureTargetPaintAsset(target, target.SceneTarget, "Create Scene Paint", out VertexPaintAsset scenePaint, out string createError),
                    Is.True,
                    createError);

                Color32[] pushedColors = CreateColorBuffer(meshAsset.vertexCount, new Color32(150, 90, 30, 255));
                scenePaint.OverwriteColors(pushedColors);
                VertexPaintEditorUtility.RebuildStreamMesh(scenePaint);

                Assert.That(
                    VertexPaintEditorUtility.TryCopyColorBufferToTarget(target, target.ContainerTarget, pushedColors, "Push Scene To Container Prefab", out string pushError),
                    Is.True,
                    pushError);

                GameObject loadedRoomRoot = PrefabUtility.LoadPrefabContents(roomPrefabPath);
                try
                {
                    GameObject loadedRendererObject = loadedRoomRoot.transform.Find("ModuleRenderer").gameObject;
                    VertexPaintBinding containerBinding = loadedRendererObject.GetComponent<VertexPaintBinding>();
                    Assert.That(containerBinding, Is.Not.Null);
                    Assert.That(containerBinding.PaintAsset, Is.Not.Null);
                    Assert.That(containerBinding.PaintAsset.GetColorsCopy(), Is.EqualTo(pushedColors));
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(loadedRoomRoot);
                }

                VertexPainterWindow.TargetDescriptor refreshedSceneTarget = CreateSceneTargetDescriptor(selectedRendererObject);
                Assert.That(refreshedSceneTarget.SceneTarget.OwnsPaint, Is.True);
                Assert.That(refreshedSceneTarget.SceneTarget.PaintAsset, Is.SameAs(scenePaint));

                Assert.That(
                    VertexPaintEditorUtility.TryClearTargetOwnership(refreshedSceneTarget, refreshedSceneTarget.SceneTarget, "Pull From Container Prefab", out string clearError),
                    Is.True,
                    clearError);

                VertexPainterWindow.TargetDescriptor pulledTarget = CreateSceneTargetDescriptor(selectedRendererObject);
                Assert.That(pulledTarget.SceneTarget.OwnsPaint, Is.False);
                Assert.That(pulledTarget.VisiblePaintOwner, Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));
                Assert.That(pulledTarget.ContainerTarget.PaintAsset, Is.Not.Null);
                Assert.That(pulledTarget.ContainerTarget.PaintAsset.GetColorsCopy(), Is.EqualTo(pushedColors));

                UnityEngine.Object.DestroyImmediate(roomInstance);
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        [Ignore("Synced container mode removes push/pull authoring.")]
        public void CopyColorBufferToContainer_CreatesContainerBinding_WhenFoundationHasNoBinding()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                string meshPath = $"{tempRoot}/TestMesh.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";
                string roomPrefabPath = $"{tempRoot}/Room.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset);

                GameObject roomRoot = new("Room");
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                moduleInstance.transform.SetParent(roomRoot.transform, false);
                PrefabUtility.SaveAsPrefabAsset(roomRoot, roomPrefabPath);
                UnityEngine.Object.DestroyImmediate(roomRoot);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject roomInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(roomPrefabPath));
                GameObject selectedRendererObject = roomInstance.transform.Find("ModuleRenderer").gameObject;
                VertexPainterWindow.TargetDescriptor target = CreateSceneTargetDescriptor(selectedRendererObject);

                Assert.That(
                    VertexPaintEditorUtility.TryEnsureTargetPaintAsset(target, target.SceneTarget, "Create Scene Paint", out VertexPaintAsset scenePaint, out string createError),
                    Is.True,
                    createError);

                Color32[] pushedColors = CreateColorBuffer(meshAsset.vertexCount, new Color32(90, 140, 190, 255));
                scenePaint.OverwriteColors(pushedColors);
                VertexPaintEditorUtility.RebuildStreamMesh(scenePaint);

                Assert.That(
                    VertexPaintEditorUtility.TryCopyColorBufferToTarget(target, target.ContainerTarget, pushedColors, "Push Scene To Container Prefab", out string pushError),
                    Is.True,
                    pushError);

                GameObject loadedRoomRoot = PrefabUtility.LoadPrefabContents(roomPrefabPath);
                try
                {
                    GameObject loadedRendererObject = loadedRoomRoot.transform.Find("ModuleRenderer").gameObject;
                    VertexPaintBinding containerBinding = loadedRendererObject.GetComponent<VertexPaintBinding>();
                    Assert.That(containerBinding, Is.Not.Null);
                    Assert.That(containerBinding.PaintAsset, Is.Not.Null);
                    Assert.That(containerBinding.PaintAsset.GetColorsCopy(), Is.EqualTo(pushedColors));
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(loadedRoomRoot);
                }

                VertexPainterWindow.TargetDescriptor refreshedSceneTarget = CreateSceneTargetDescriptor(selectedRendererObject);
                Assert.That(refreshedSceneTarget.SceneTarget.OwnsPaint, Is.True);
                Assert.That(refreshedSceneTarget.SceneTarget.PaintAsset, Is.SameAs(scenePaint));

                UnityEngine.Object.DestroyImmediate(roomInstance);
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void ContainerLiveEdit_ForksInheritedPaint_AndClearDoesNotResurrectOldColors()
        {
            string tempRoot = CreateTempFolder();
            VertexPainterSettings settings = VertexPainterSettings.instance;
            string originalRoot = settings.GeneratedDataRoot;

            try
            {
                settings.GeneratedDataRoot = $"{tempRoot}/Generated";
                settings.SaveIfDirty();

                string meshPath = $"{tempRoot}/TestMesh.asset";
                string foundationPaintPath = $"{tempRoot}/FoundationPaint.asset";
                string modulePrefabPath = $"{tempRoot}/Module.prefab";
                string roomPrefabPath = $"{tempRoot}/Room.prefab";

                Mesh meshAsset = CreateMeshAsset(meshPath);
                VertexPaintAsset foundationPaint = CreatePaintAsset(foundationPaintPath, meshAsset);
                foundationPaint.OverwriteColors(CreateColorBuffer(meshAsset.vertexCount, new Color32(40, 80, 120, 255)));
                CreatePrefab(modulePrefabPath, "ModuleRenderer", meshAsset, foundationPaint);

                GameObject roomRoot = new("Room");
                GameObject moduleInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));
                moduleInstance.transform.SetParent(roomRoot.transform, false);
                PrefabUtility.SaveAsPrefabAsset(roomRoot, roomPrefabPath);
                UnityEngine.Object.DestroyImmediate(roomRoot);

                GameObject loadedRoomRoot = PrefabUtility.LoadPrefabContents(roomPrefabPath);
                try
                {
                    GameObject selectedRendererObject = loadedRoomRoot.transform.Find("ModuleRenderer").gameObject;
                    VertexPainterWindow.TargetDescriptor target = CreateLiveContainerTargetDescriptor(
                        selectedRendererObject,
                        roomPrefabPath,
                        AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));

                    Assert.That(
                        VertexPaintEditorUtility.TryEnsureTargetPaintAsset(target, target.ContainerTarget, "Create Container Paint", out VertexPaintAsset containerPaint, out string createError),
                        Is.True,
                        createError);

                    Assert.That(containerPaint.GetColorsCopy(), Is.EqualTo(foundationPaint.GetColorsCopy()));

                    containerPaint.OverwriteColors(CreateColorBuffer(meshAsset.vertexCount, new Color32(180, 30, 30, 255)));
                    VertexPaintEditorUtility.RebuildStreamMesh(containerPaint);
                    string containerPaintPath = AssetDatabase.GetAssetPath(containerPaint);

                    PrefabUtility.SaveAsPrefabAsset(loadedRoomRoot, roomPrefabPath);

                    Assert.That(
                        VertexPaintEditorUtility.TryClearTargetOwnership(target, target.ContainerTarget, "Clear Container Paint", out string clearError),
                        Is.True,
                        clearError);

                    Assert.That(AssetDatabase.LoadAssetAtPath<VertexPaintAsset>(containerPaintPath), Is.Null);

                    VertexPainterWindow.TargetDescriptor refreshedTarget = CreateLiveContainerTargetDescriptor(
                        selectedRendererObject,
                        roomPrefabPath,
                        AssetDatabase.LoadAssetAtPath<GameObject>(modulePrefabPath));

                    Assert.That(refreshedTarget.ContainerTarget.OwnsPaint, Is.False);
                    Assert.That(refreshedTarget.VisiblePaintOwner, Is.EqualTo(VertexPaintOwnershipLevel.FoundationPrefab));

                    Assert.That(
                        VertexPaintEditorUtility.TryEnsureTargetPaintAsset(refreshedTarget, refreshedTarget.ContainerTarget, "Recreate Container Paint", out VertexPaintAsset recreatedContainerPaint, out string recreateError),
                        Is.True,
                        recreateError);

                    Assert.That(recreatedContainerPaint.GetColorsCopy(), Is.EqualTo(foundationPaint.GetColorsCopy()));
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(loadedRoomRoot);
                }
            }
            finally
            {
                settings.GeneratedDataRoot = originalRoot;
                settings.SaveIfDirty();
                AssetDatabase.DeleteAsset(tempRoot);
            }
        }

        [Test]
        public void BuildPaintAssetPath_UsesContainerAndFoundationCategoryFolders()
        {
            Mesh mesh = CreateTriangleMesh("PathMesh");
            GameObject targetObject = new("PathTarget");
            targetObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            targetObject.AddComponent<MeshRenderer>();

            try
            {
                VertexPainterSettings settings = VertexPainterSettings.instance;
                VertexPaintResolvedTarget sceneTarget = new(
                    VertexPaintOwnershipLevel.SceneInstance,
                    "Scene Instance",
                    "Scenes",
                    true,
                    string.Empty,
                    "SampleScene",
                    "SampleScene",
                    "SceneObject",
                    string.Empty,
                    string.Empty,
                    true,
                    targetObject,
                    false,
                    false,
                    false);

                VertexPaintResolvedTarget containerTarget = new(
                    VertexPaintOwnershipLevel.ContainerPrefab,
                    "Container Prefab",
                    "ContainerPrefabs",
                    true,
                    string.Empty,
                    "RoomVariant",
                    "Assets/RoomVariant.prefab",
                    "ContainerObject",
                    "Assets/RoomVariant.prefab",
                    "ModuleRenderer",
                    false,
                    null,
                    false,
                    false,
                    false);

                VertexPaintResolvedTarget foundationTarget = new(
                    VertexPaintOwnershipLevel.FoundationPrefab,
                    "Foundation Prefab",
                    "FoundationPrefabs",
                    true,
                    string.Empty,
                    "Module",
                    "Assets/Module.prefab",
                    "FoundationObject",
                    "Assets/Module.prefab",
                    "ModuleRenderer",
                    false,
                    null,
                    false,
                    false,
                    false);

                string scenePath = VertexPaintEditorUtility.BuildPaintAssetPath(settings, targetObject, mesh, sceneTarget);
                string containerPath = VertexPaintEditorUtility.BuildPaintAssetPath(settings, targetObject, mesh, containerTarget);
                string foundationPath = VertexPaintEditorUtility.BuildPaintAssetPath(settings, targetObject, mesh, foundationTarget);

                Assert.That(scenePath, Does.Contain("/Scenes/"));
                Assert.That(containerPath, Does.Contain("/ContainerPrefabs/"));
                Assert.That(foundationPath, Does.Contain("/FoundationPrefabs/"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(targetObject);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void VisiblePaintOwner_UsesNearestWinsAndHonorsSuppressingOverrides()
        {
            VertexPaintResolvedTarget sceneTarget = new(
                VertexPaintOwnershipLevel.SceneInstance,
                "Scene Instance",
                "Scenes",
                true,
                string.Empty,
                "Scene",
                "Scene",
                "SceneObject",
                string.Empty,
                string.Empty,
                true,
                null,
                false,
                false,
                false);

            VertexPaintResolvedTarget containerTarget = new(
                VertexPaintOwnershipLevel.ContainerPrefab,
                "Container Prefab",
                "ContainerPrefabs",
                true,
                string.Empty,
                "Container",
                "Container",
                "ContainerObject",
                "Container",
                string.Empty,
                false,
                null,
                true,
                true,
                false);

            VertexPaintResolvedTarget foundationTarget = new(
                VertexPaintOwnershipLevel.FoundationPrefab,
                "Foundation Prefab",
                "FoundationPrefabs",
                true,
                string.Empty,
                "Foundation",
                "Foundation",
                "FoundationObject",
                "Foundation",
                string.Empty,
                false,
                null,
                true,
                false,
                false);

            Assert.That(
                VertexPaintAuthoringContextUtility.GetVisiblePaintOwner(sceneTarget, containerTarget, foundationTarget),
                Is.EqualTo(VertexPaintOwnershipLevel.ContainerPrefab));

            sceneTarget = new VertexPaintResolvedTarget(
                VertexPaintOwnershipLevel.SceneInstance,
                "Scene Instance",
                "Scenes",
                true,
                string.Empty,
                "Scene",
                "Scene",
                "SceneObject",
                string.Empty,
                string.Empty,
                true,
                null,
                true,
                true,
                false);

            Assert.That(
                VertexPaintAuthoringContextUtility.GetVisiblePaintOwner(sceneTarget, containerTarget, foundationTarget),
                Is.EqualTo(VertexPaintOwnershipLevel.SceneInstance));

            sceneTarget = new VertexPaintResolvedTarget(
                VertexPaintOwnershipLevel.SceneInstance,
                "Scene Instance",
                "Scenes",
                true,
                string.Empty,
                "Scene",
                "Scene",
                "SceneObject",
                string.Empty,
                string.Empty,
                true,
                null,
                false,
                false,
                false);

            containerTarget = new VertexPaintResolvedTarget(
                VertexPaintOwnershipLevel.ContainerPrefab,
                "Container Prefab",
                "ContainerPrefabs",
                true,
                string.Empty,
                "Container",
                "Container",
                "ContainerObject",
                "Container",
                string.Empty,
                false,
                null,
                false,
                false,
                true);

            Assert.That(
                VertexPaintAuthoringContextUtility.GetVisiblePaintOwner(sceneTarget, containerTarget, foundationTarget),
                Is.Null);
        }

        private static Mesh CreateMeshAsset(string assetPath)
        {
            Mesh mesh = CreateTriangleMesh("TestMesh");
            AssetDatabase.CreateAsset(mesh, assetPath);
            return AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        }

        private static VertexPaintAsset CreatePaintAsset(string assetPath, Mesh mesh)
        {
            VertexPaintAsset asset = ScriptableObject.CreateInstance<VertexPaintAsset>();
            asset.Initialize(mesh, VertexPaintChannelUtility.DefaultColor);
            AssetDatabase.CreateAsset(asset, assetPath);
            return AssetDatabase.LoadAssetAtPath<VertexPaintAsset>(assetPath);
        }

        private static Color32[] CreateColorBuffer(int vertexCount, Color32 color)
        {
            Color32[] colors = new Color32[vertexCount];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = color;
            }

            return colors;
        }

        private static void CreatePrefab(string prefabPath, string rootName, Mesh mesh, VertexPaintAsset paintAsset = null)
        {
            GameObject root = new(rootName);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            root.AddComponent<MeshRenderer>();
            if (paintAsset != null)
            {
                VertexPaintBinding binding = root.AddComponent<VertexPaintBinding>();
                binding.SetPaintAsset(paintAsset);
            }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
        }

        private static void CreateThreeLevelNestedPrefabs(
            string tempRoot,
            out Mesh meshAsset,
            out string leafPrefabPath,
            out string midPrefabPath,
            out string outerPrefabPath)
        {
            string meshPath = $"{tempRoot}/TestMesh.asset";
            leafPrefabPath = $"{tempRoot}/Leaf.prefab";
            midPrefabPath = $"{tempRoot}/Mid.prefab";
            outerPrefabPath = $"{tempRoot}/Outer.prefab";

            meshAsset = CreateMeshAsset(meshPath);
            CreatePrefab(leafPrefabPath, "Leaf", meshAsset);

            GameObject midRoot = new("Mid");
            GameObject leafInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(leafPrefabPath));
            leafInstance.transform.SetParent(midRoot.transform, false);
            PrefabUtility.SaveAsPrefabAsset(midRoot, midPrefabPath);
            UnityEngine.Object.DestroyImmediate(midRoot);

            GameObject outerRoot = new("Outer");
            GameObject midInstance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(midPrefabPath));
            midInstance.transform.SetParent(outerRoot.transform, false);
            PrefabUtility.SaveAsPrefabAsset(outerRoot, outerPrefabPath);
            UnityEngine.Object.DestroyImmediate(outerRoot);
        }

        private static VertexPainterWindow.TargetDescriptor CreateSceneTargetDescriptor(
            GameObject selectedRendererObject,
            GameObject authoringRoot = null)
        {
            GameObject resolvedAuthoringRoot = authoringRoot != null
                ? VertexPaintEditorUtility.ResolveSelectedAuthoringRoot(authoringRoot)
                : VertexPaintEditorUtility.ResolveSelectedAuthoringRoot(selectedRendererObject);

            VertexPaintResolvedTarget sceneTarget = VertexPaintAuthoringContextUtility.Evaluate(selectedRendererObject, VertexPaintOwnershipLevel.SceneInstance, resolvedAuthoringRoot);
            VertexPaintResolvedTarget containerTarget = VertexPaintAuthoringContextUtility.Evaluate(selectedRendererObject, VertexPaintOwnershipLevel.ContainerPrefab, resolvedAuthoringRoot);
            VertexPaintResolvedTarget foundationTarget = VertexPaintAuthoringContextUtility.Evaluate(selectedRendererObject, VertexPaintOwnershipLevel.FoundationPrefab, resolvedAuthoringRoot);
            sceneTarget = VertexPaintAuthoringContextUtility.NormalizeSceneTarget(sceneTarget, containerTarget, foundationTarget);
            VertexPaintResolvedTarget currentPaintTarget = VertexPaintAuthoringContextUtility.ResolveCurrentPaintTarget(
                selectedRendererObject,
                sceneTarget,
                containerTarget,
                foundationTarget);
            VertexPaintOwnershipLevel? visibleOwner = VertexPaintAuthoringContextUtility.GetVisiblePaintOwner(sceneTarget, containerTarget, foundationTarget);

            return new VertexPainterWindow.TargetDescriptor(
                selectedRendererObject,
                resolvedAuthoringRoot,
                selectedRendererObject.GetComponent<MeshFilter>(),
                selectedRendererObject.GetComponent<MeshRenderer>(),
                selectedRendererObject.GetComponent<MeshFilter>().sharedMesh,
                sceneTarget,
                containerTarget,
                foundationTarget,
                currentPaintTarget,
                visibleOwner,
                null);
        }

        private static VertexPainterWindow.TargetDescriptor CreateLiveContainerTargetDescriptor(
            GameObject selectedRendererObject,
            string containerPrefabPath,
            GameObject foundationAssetObject)
        {
            string normalizedContainerPath = VertexPaintEditorUtility.NormalizeAssetPath(containerPrefabPath);
            string normalizedFoundationPath = VertexPaintEditorUtility.NormalizeAssetPath(AssetDatabase.GetAssetPath(foundationAssetObject));
            string relativePath = VertexPaintAuthoringContextUtility.GetHierarchyRelativePath(selectedRendererObject.transform.root, selectedRendererObject.transform);
            VertexPaintBinding liveBinding = selectedRendererObject.GetComponent<VertexPaintBinding>();
            GameObject sourceObject = PrefabUtility.GetCorrespondingObjectFromSource(selectedRendererObject);
            VertexPaintBinding sourceBinding = sourceObject != null ? sourceObject.GetComponent<VertexPaintBinding>() : null;
            VertexPaintBinding foundationBinding = foundationAssetObject != null ? foundationAssetObject.GetComponent<VertexPaintBinding>() : null;
            bool containerHasPaint = liveBinding != null && liveBinding.PaintAsset != null;
            bool containerOwnsPaint = false;
            bool containerSuppressesInheritedPaint = false;

            if (sourceBinding == null)
            {
                containerOwnsPaint = containerHasPaint;
            }
            else if (liveBinding != null && liveBinding.PaintAsset != sourceBinding.PaintAsset)
            {
                containerOwnsPaint = liveBinding.PaintAsset != null;
                containerSuppressesInheritedPaint = liveBinding.PaintAsset == null;
            }

            VertexPaintResolvedTarget unavailableSceneTarget = new(
                VertexPaintOwnershipLevel.SceneInstance,
                "Scene Instance",
                "Scenes",
                false,
                "Scene Instance only exists on placed scene objects.",
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

            VertexPaintResolvedTarget containerTarget = new(
                VertexPaintOwnershipLevel.ContainerPrefab,
                "Container Prefab",
                "ContainerPrefabs",
                true,
                string.Empty,
                Path.GetFileNameWithoutExtension(normalizedContainerPath),
                normalizedContainerPath,
                $"Container:{relativePath}",
                normalizedContainerPath,
                relativePath,
                true,
                selectedRendererObject,
                containerHasPaint,
                containerOwnsPaint,
                containerSuppressesInheritedPaint);

            VertexPaintResolvedTarget foundationTarget = new(
                VertexPaintOwnershipLevel.FoundationPrefab,
                "Foundation Prefab",
                "FoundationPrefabs",
                true,
                string.Empty,
                Path.GetFileNameWithoutExtension(normalizedFoundationPath),
                normalizedFoundationPath,
                $"Foundation:{relativePath}",
                normalizedFoundationPath,
                string.Empty,
                false,
                foundationAssetObject,
                foundationBinding != null && foundationBinding.PaintAsset != null,
                foundationBinding != null && foundationBinding.PaintAsset != null,
                false);

            VertexPaintOwnershipLevel? visibleOwner = containerOwnsPaint
                ? VertexPaintOwnershipLevel.ContainerPrefab
                : containerSuppressesInheritedPaint
                    ? null
                    : foundationTarget.HasPaint
                        ? VertexPaintOwnershipLevel.FoundationPrefab
                        : null;

            return new VertexPainterWindow.TargetDescriptor(
                selectedRendererObject,
                selectedRendererObject.transform.root.gameObject,
                selectedRendererObject.GetComponent<MeshFilter>(),
                selectedRendererObject.GetComponent<MeshRenderer>(),
                selectedRendererObject.GetComponent<MeshFilter>().sharedMesh,
                unavailableSceneTarget,
                containerTarget,
                foundationTarget,
                containerTarget,
                visibleOwner,
                null);
        }

        private static Mesh CreateTriangleMesh(string name)
        {
            Mesh mesh = new() { name = name };
            mesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.right,
                Vector3.up
            };
            mesh.triangles = new[] { 0, 1, 2 };
            return mesh;
        }

        private static string CreateTempFolder()
        {
            EnsureFolderExists("Assets/VertexPainterTests");
            EnsureFolderExists(TempRootFolder);

            string leaf = Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(TempRootFolder, leaf);
            return $"{TempRootFolder}/{leaf}";
        }

        private static void EnsureFolderExists(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                return;
            }

            int lastSlash = assetPath.LastIndexOf('/');
            string parent = lastSlash > 0 ? assetPath.Substring(0, lastSlash) : "Assets";
            string leaf = assetPath.Substring(lastSlash + 1);
            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolderExists(parent);
            }

            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}

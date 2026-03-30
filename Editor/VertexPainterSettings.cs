using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace JuiceAI.VertexPainter.Editor
{
    [FilePath("ProjectSettings/JuiceAI.VertexPainterSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class VertexPainterSettings : ScriptableSingleton<VertexPainterSettings>
    {
        [SerializeField] private string generatedDataRoot = "Assets/VertexPainter";
        [SerializeField] private float defaultBrushSize = 1.5f;
        [SerializeField] private float defaultBrushStrength = 0.35f;
        [SerializeField] private float defaultBrushSpacing = 0.25f;
        [SerializeField] private float defaultBrushFalloff = 1.5f;
        [SerializeField] private bool scenePreviewEnabled = true;
        [FormerlySerializedAs("scenePreviewDotSize")]
        [SerializeField] private float scenePreviewOpacity = 0.28f;

        public string GeneratedDataRoot
        {
            get => VertexPaintEditorUtility.NormalizeAssetPath(generatedDataRoot);
            set => generatedDataRoot = VertexPaintEditorUtility.NormalizeAssetPath(value);
        }

        public float DefaultBrushSize
        {
            get => defaultBrushSize;
            set => defaultBrushSize = Mathf.Max(0.01f, value);
        }

        public float DefaultBrushStrength
        {
            get => defaultBrushStrength;
            set => defaultBrushStrength = Mathf.Clamp01(value);
        }

        public float DefaultBrushSpacing
        {
            get => defaultBrushSpacing;
            set => defaultBrushSpacing = Mathf.Clamp(value, 0.05f, 1f);
        }

        public float DefaultBrushFalloff
        {
            get => defaultBrushFalloff;
            set => defaultBrushFalloff = Mathf.Max(0.01f, value);
        }

        public bool ScenePreviewEnabled
        {
            get => scenePreviewEnabled;
            set => scenePreviewEnabled = value;
        }

        public float ScenePreviewOpacity
        {
            get => scenePreviewOpacity;
            set => scenePreviewOpacity = Mathf.Clamp01(value);
        }

        public bool TryValidateGeneratedDataRoot(out string error)
        {
            string candidate = GeneratedDataRoot;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                error = "The generated data root cannot be empty.";
                return false;
            }

            if (!candidate.StartsWith("Assets"))
            {
                error = "The generated data root must be inside the project's Assets folder.";
                return false;
            }

            if (VertexPaintEditorUtility.TryGetModuleRootPath(out string moduleRoot) &&
                candidate.StartsWith(moduleRoot))
            {
                error = "Generated paint assets cannot be written into the VertexPainter code folder.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        public void SaveIfDirty()
        {
            Save(true);
        }
    }
}

using UnityEditor;
using UnityEngine;

namespace JuiceAI.VertexPainter.Editor
{
    [CustomEditor(typeof(VertexPaintBinding))]
    public sealed class VertexPaintBindingEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            VertexPaintBinding binding = (VertexPaintBinding)target;
            binding.EvaluateStatus(out string message);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Paint Asset", binding.PaintAsset, typeof(VertexPaintAsset), false);
                EditorGUILayout.EnumPopup("Status", binding.BindingStatus);
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                EditorGUILayout.HelpBox(
                    message,
                    binding.BindingStatus == VertexPaintBindingStatus.Valid ? MessageType.Info : MessageType.Warning);
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("Refresh Binding"))
            {
                binding.RefreshBinding();
                EditorUtility.SetDirty(binding);
            }

            if (GUILayout.Button("Open Vertex Painter"))
            {
                VertexPainterWindow.ShowWindow();
            }
        }
    }
}

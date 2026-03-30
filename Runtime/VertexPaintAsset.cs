using System;
using UnityEngine;

namespace JuiceAI.VertexPainter
{
    [PreferBinarySerialization]
    public sealed class VertexPaintAsset : ScriptableObject
    {
        [SerializeField] private Mesh sourceMesh;
        [SerializeField] private int sourceVertexCount;
        [SerializeField] private Color32[] colors = Array.Empty<Color32>();
        [SerializeField] private Mesh streamMesh;

        public Mesh SourceMesh => sourceMesh;
        public int SourceVertexCount => sourceVertexCount;
        public Mesh StreamMesh => streamMesh;
        public bool HasColorData => colors != null && colors.Length == sourceVertexCount;

        public Color32[] GetColorsCopy()
        {
            if (colors == null || colors.Length == 0)
            {
                return Array.Empty<Color32>();
            }

            Color32[] copy = new Color32[colors.Length];
            Array.Copy(colors, copy, colors.Length);
            return copy;
        }

        public void Initialize(Mesh mesh, Color32 fillColor)
        {
            sourceMesh = mesh;
            sourceVertexCount = mesh != null ? mesh.vertexCount : 0;

            if (sourceVertexCount <= 0)
            {
                colors = Array.Empty<Color32>();
                return;
            }

            colors = new Color32[sourceVertexCount];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = fillColor;
            }
        }

        public void OverwriteColors(Color32[] newColors)
        {
            if (newColors == null)
            {
                colors = Array.Empty<Color32>();
                return;
            }

            if (newColors.Length != sourceVertexCount)
            {
                throw new ArgumentException(
                    $"Vertex paint color buffer length {newColors.Length} does not match source vertex count {sourceVertexCount}.",
                    nameof(newColors));
            }

            colors = new Color32[newColors.Length];
            Array.Copy(newColors, colors, newColors.Length);
        }

        public void AssignStreamMesh(Mesh mesh)
        {
            streamMesh = mesh;
        }

        public bool MatchesSourceMesh(Mesh mesh)
        {
            return sourceMesh != null &&
                   mesh != null &&
                   sourceMesh == mesh &&
                   sourceVertexCount == mesh.vertexCount;
        }

        public string GetValidationMessage(Mesh currentMesh)
        {
            if (sourceMesh == null)
            {
                return "The paint asset is missing its source mesh.";
            }

            if (currentMesh == null)
            {
                return "The target renderer is missing a source mesh.";
            }

            if (sourceMesh != currentMesh)
            {
                return "The target renderer no longer uses the mesh this paint asset was created from.";
            }

            if (currentMesh.vertexCount != sourceVertexCount)
            {
                return "The source mesh vertex count changed. Recreate the paint binding.";
            }

            if (!HasColorData)
            {
                return "The paint asset is missing vertex color data.";
            }

            if (streamMesh == null)
            {
                return "The paint asset is missing its generated stream mesh.";
            }

            return string.Empty;
        }
    }
}

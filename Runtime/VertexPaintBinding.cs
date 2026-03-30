using UnityEngine;

namespace JuiceAI.VertexPainter
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class VertexPaintBinding : MonoBehaviour
    {
        [SerializeField] private VertexPaintAsset paintAsset;

        private MeshFilter cachedMeshFilter;
        private MeshRenderer cachedRenderer;

        public VertexPaintAsset PaintAsset => paintAsset;
        public MeshFilter MeshFilter => cachedMeshFilter != null ? cachedMeshFilter : cachedMeshFilter = GetComponent<MeshFilter>();
        public MeshRenderer MeshRenderer => cachedRenderer != null ? cachedRenderer : cachedRenderer = GetComponent<MeshRenderer>();
        public VertexPaintBindingStatus BindingStatus => EvaluateStatus(out _);
        public bool HasValidBinding => BindingStatus == VertexPaintBindingStatus.Valid;

        public void SetPaintAsset(VertexPaintAsset asset)
        {
            paintAsset = asset;
            RefreshBinding();
        }

        public void ClearPaintAsset()
        {
            paintAsset = null;
            RefreshBinding();
        }

        public void RefreshBinding()
        {
            MeshRenderer renderer = MeshRenderer;
            if (renderer == null)
            {
                return;
            }

            renderer.additionalVertexStreams = TryGetValidStreamMesh(out Mesh streamMesh)
                ? streamMesh
                : null;
        }

        public bool TryGetSourceMesh(out Mesh mesh)
        {
            mesh = MeshFilter != null ? MeshFilter.sharedMesh : null;
            return mesh != null;
        }

        public bool TryGetValidStreamMesh(out Mesh streamMesh)
        {
            streamMesh = null;
            if (paintAsset == null)
            {
                return false;
            }

            if (!TryGetSourceMesh(out Mesh currentMesh))
            {
                return false;
            }

            if (!paintAsset.MatchesSourceMesh(currentMesh))
            {
                return false;
            }

            streamMesh = paintAsset.StreamMesh;
            return streamMesh != null;
        }

        public VertexPaintBindingStatus EvaluateStatus(out string message)
        {
            if (MeshFilter == null || MeshRenderer == null)
            {
                message = "Vertex paint bindings require a MeshFilter and MeshRenderer on the same GameObject.";
                return VertexPaintBindingStatus.MissingComponents;
            }

            if (paintAsset == null)
            {
                message = "No vertex paint asset is assigned.";
                return VertexPaintBindingStatus.Unbound;
            }

            Mesh currentMesh = MeshFilter.sharedMesh;
            if (currentMesh == null)
            {
                message = "The target renderer is missing a source mesh.";
                return VertexPaintBindingStatus.MissingSourceMesh;
            }

            if (paintAsset.SourceMesh == null)
            {
                message = "The assigned paint asset is missing its source mesh.";
                return VertexPaintBindingStatus.MissingSourceMesh;
            }

            if (!paintAsset.MatchesSourceMesh(currentMesh))
            {
                message = paintAsset.GetValidationMessage(currentMesh);
                return VertexPaintBindingStatus.SourceMeshMismatch;
            }

            if (!paintAsset.HasColorData)
            {
                message = "The assigned paint asset is missing vertex color data.";
                return VertexPaintBindingStatus.MissingColorData;
            }

            if (paintAsset.StreamMesh == null)
            {
                message = "The assigned paint asset is missing its generated stream mesh.";
                return VertexPaintBindingStatus.MissingStreamMesh;
            }

            message = string.Empty;
            return VertexPaintBindingStatus.Valid;
        }

        private void OnEnable()
        {
            RefreshBinding();
        }

        private void OnValidate()
        {
            cachedMeshFilter = null;
            cachedRenderer = null;
            RefreshBinding();
        }
    }
}

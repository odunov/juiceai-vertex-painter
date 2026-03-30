using NUnit.Framework;
using UnityEngine;

namespace JuiceAI.VertexPainter.Tests.Editor
{
    public class VertexPaintRuntimeTests
    {
        [Test]
        public void VertexPaintAsset_MatchesSourceMesh_WhenMeshReferenceAndVertexCountMatch()
        {
            Mesh mesh = new() { name = "TestMesh" };
            mesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.right,
                Vector3.up
            };
            mesh.triangles = new[] { 0, 1, 2 };

            VertexPaintAsset asset = ScriptableObject.CreateInstance<VertexPaintAsset>();
            asset.Initialize(mesh, VertexPaintChannelUtility.DefaultColor);

            Assert.That(asset.MatchesSourceMesh(mesh), Is.True);

            Object.DestroyImmediate(asset);
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void VertexPaintBinding_RefreshBinding_AssignsAdditionalVertexStreams_WhenBindingIsValid()
        {
            GameObject gameObject = new("VertexPaintBindingTest");
            MeshFilter meshFilter = gameObject.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = gameObject.AddComponent<MeshRenderer>();
            VertexPaintBinding binding = gameObject.AddComponent<VertexPaintBinding>();

            Mesh sourceMesh = new() { name = "SourceMesh" };
            sourceMesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.right,
                Vector3.up
            };
            sourceMesh.triangles = new[] { 0, 1, 2 };
            meshFilter.sharedMesh = sourceMesh;

            Mesh streamMesh = new() { name = "StreamMesh" };
            streamMesh.vertices = sourceMesh.vertices;
            streamMesh.colors32 = new[]
            {
                new Color32(255, 0, 0, 255),
                new Color32(255, 0, 0, 255),
                new Color32(255, 0, 0, 255)
            };

            VertexPaintAsset asset = ScriptableObject.CreateInstance<VertexPaintAsset>();
            asset.Initialize(sourceMesh, VertexPaintChannelUtility.DefaultColor);
            asset.AssignStreamMesh(streamMesh);

            binding.SetPaintAsset(asset);
            binding.RefreshBinding();

            Assert.That(meshRenderer.additionalVertexStreams, Is.SameAs(streamMesh));

            Object.DestroyImmediate(asset);
            Object.DestroyImmediate(streamMesh);
            Object.DestroyImmediate(sourceMesh);
            Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void VertexPaintAsset_OverwriteColors_RejectsVertexCountMismatch()
        {
            Mesh mesh = new() { name = "MismatchMesh" };
            mesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.right,
                Vector3.up
            };
            mesh.triangles = new[] { 0, 1, 2 };

            VertexPaintAsset asset = ScriptableObject.CreateInstance<VertexPaintAsset>();
            asset.Initialize(mesh, VertexPaintChannelUtility.DefaultColor);

            Assert.That(
                () => asset.OverwriteColors(new[] { new Color32(255, 255, 255, 255) }),
                Throws.TypeOf<System.ArgumentException>());

            Object.DestroyImmediate(asset);
            Object.DestroyImmediate(mesh);
        }
    }
}

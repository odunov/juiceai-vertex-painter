using UnityEngine;

namespace JuiceAI.VertexPainter.Editor
{
    public interface IVertexPaintMaterialAdapter
    {
        int Order { get; }
        string DisplayName { get; }

        bool CanHandle(Renderer renderer, Material[] materials);
        VertexPaintChannel GetDefaultChannel(Renderer renderer, Material[] materials);
        string GetDescription(Renderer renderer, Material[] materials);
        string GetWarning(Renderer renderer, Material[] materials);
    }
}

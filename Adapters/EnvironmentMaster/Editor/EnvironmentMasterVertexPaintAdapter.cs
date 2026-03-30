using UnityEngine;

namespace JuiceAI.VertexPainter.Editor
{
    public sealed class EnvironmentMasterVertexPaintAdapter : IVertexPaintMaterialAdapter
    {
        private const string ShaderName = "JuiceAI/Environment/JA_EnvironmentMaster";

        public int Order => 100;
        public string DisplayName => "JA Environment Master";

        public bool CanHandle(Renderer renderer, Material[] materials)
        {
            if (materials == null)
            {
                return false;
            }

            foreach (Material material in materials)
            {
                if (material != null && material.shader != null && material.shader.name == ShaderName)
                {
                    return true;
                }
            }

            return false;
        }

        public VertexPaintChannel GetDefaultChannel(Renderer renderer, Material[] materials)
        {
            return VertexPaintChannel.Red;
        }

        public string GetDescription(Renderer renderer, Material[] materials)
        {
            return "JA_EnvironmentMaster uses the red vertex channel as the default top-layer blend mask.";
        }

        public string GetWarning(Renderer renderer, Material[] materials)
        {
            if (materials == null)
            {
                return string.Empty;
            }

            foreach (Material material in materials)
            {
                if (material == null || material.shader == null || material.shader.name != ShaderName)
                {
                    continue;
                }

                if (material.HasProperty("_EnableTopLayer") && material.GetFloat("_EnableTopLayer") < 0.5f)
                {
                    return "The top layer is currently disabled on at least one JA_EnvironmentMaster material.";
                }

                if (material.HasProperty("_TopUseVertexColorBlend") && material.GetFloat("_TopUseVertexColorBlend") < 0.5f)
                {
                    return "Vertex color blending is disabled on at least one JA_EnvironmentMaster material.";
                }
            }

            return string.Empty;
        }
    }
}

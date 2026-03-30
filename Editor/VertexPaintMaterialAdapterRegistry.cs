using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace JuiceAI.VertexPainter.Editor
{
    public static class VertexPaintMaterialAdapterRegistry
    {
        private static List<IVertexPaintMaterialAdapter> cachedAdapters;

        public static IReadOnlyList<IVertexPaintMaterialAdapter> GetAdapters()
        {
            if (cachedAdapters != null)
            {
                return cachedAdapters;
            }

            cachedAdapters = TypeCache
                .GetTypesDerivedFrom<IVertexPaintMaterialAdapter>()
                .Where(type => !type.IsAbstract && !type.IsInterface && type.GetConstructor(Type.EmptyTypes) != null)
                .Select(type => (IVertexPaintMaterialAdapter)Activator.CreateInstance(type))
                .OrderBy(adapter => adapter.Order)
                .ToList();

            return cachedAdapters;
        }

        public static IVertexPaintMaterialAdapter FindBestAdapter(Renderer renderer)
        {
            if (renderer == null)
            {
                return null;
            }

            Material[] materials = renderer.sharedMaterials ?? Array.Empty<Material>();
            return GetAdapters().FirstOrDefault(adapter => adapter.CanHandle(renderer, materials));
        }
    }
}

# Juice AI Vertex Painter

## Overview

Juice AI Vertex Painter lets you paint vertex color data onto meshes while keeping prefab authoring and scene authoring in sync.

When you paint a prefab instance in a scene, the tool resolves the shared prefab owner and writes paint data that can also be seen from Prefab Mode. When you paint the prefab in Prefab Mode, scene instances using that shared data see the same result.

## Data model

- Runtime data lives in `VertexPaintAsset`.
- Scene and prefab objects reference paint data through `VertexPaintBinding`.
- Generated paint assets are created under `Assets/VertexPainter` by default.

## Package contents

- `Runtime/`
- `Editor/`
- `Tests/Editor/`

Shader-specific editor adapters can be added separately by implementing `IVertexPaintMaterialAdapter` in your own project or extension package.

## Installation

Use one of these approaches:

1. Embed the package by placing it under `Packages/com.juiceai.vertex-painter`.
2. Add it from a local folder in Unity Package Manager.
3. Add it from a Git URL once the package is hosted in its own repository.

## Tests

The package includes EditMode tests under `Tests/Editor/`.

For non-embedded installs, add the package name to the project's `testables` array if you want Unity Test Framework to surface those tests.

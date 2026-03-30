# Juice AI Vertex Painter

Prefab-aware vertex painting for Unity 6.

## What it does

- Paint meshes directly in the scene or in Prefab Mode.
- Share the same paint data between prefab instances and the source prefab.
- Store generated paint data under `Assets/VertexPainter` by default.
- Apply paint at runtime through `VertexPaintBinding` and `MeshRenderer.additionalVertexStreams`.

## Package layout

- `Runtime/`: runtime data and binding components.
- `Editor/`: authoring window, asset generation, and editor tooling.
- `Adapters/`: optional editor adapters for project-specific shader workflows.
- `Tests/Editor/`: EditMode tests for authoring and runtime behavior.

## Using it in another project

You can use this package in three common ways:

1. Copy this folder into `Packages/com.juiceai.vertex-painter` as an embedded package.
2. Install it from a local folder with Unity Package Manager.
3. Install it from a Git repository once this package folder lives in its own repo.

## Notes

- Generated paint assets are project content and should stay under `Assets/`, not inside the package.
- If you want to run the package tests from a non-embedded install, add `com.juiceai.vertex-painter` to the project's `testables` list.

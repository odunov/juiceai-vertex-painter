namespace JuiceAI.VertexPainter
{
    public enum VertexPaintBindingStatus
    {
        Unbound = 0,
        MissingComponents = 1,
        MissingSourceMesh = 2,
        SourceMeshMismatch = 3,
        MissingColorData = 4,
        MissingStreamMesh = 5,
        Valid = 6
    }
}

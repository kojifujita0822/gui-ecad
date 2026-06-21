using GuiEcad.Model;

namespace GuiEcad.Tests;

internal static class TestHelper
{
    internal static ElementInstance El(ElementKind kind, int row, int col,
        string? dev = null, int width = 1)
        => new()
        {
            Kind = kind,
            Pos = new GridPos(row, col),
            CellWidth = width,
            DeviceName = dev,
        };
}

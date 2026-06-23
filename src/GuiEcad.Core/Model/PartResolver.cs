namespace GuiEcad.Model;

/// <summary>
/// 要素の接続点・電気的役割を「組込み種別」と「自作パーツ」で統一的に解決する。
/// 自作パーツ（<see cref="ElementInstance.PartId"/> 指定）は <see cref="PartLibrary"/> から、
/// それ以外は <see cref="ElementCatalog"/> から取得する。
/// </summary>
public static class PartResolver
{
    public static IReadOnlyList<PortDef> Ports(ElementInstance e, PartLibrary? lib)
    {
        var part = lib?.Get(e.PartId);
        if (part is not null) return part.Ports;
        return ElementCatalog.Ports(e.Kind, Math.Max(1, e.CellWidth));
    }

    /// <summary>要素が占める列境界の左端・右端（最左/最右ポート境界）。</summary>
    public static (int Left, int Right) BoundarySpan(ElementInstance e, PartLibrary? lib)
    {
        var ports = Ports(e, lib);
        // 接続点を持たない記号（主回路3極記号・ゼロポート自作パーツ）は CellWidth で占有幅を決める。
        if (ports.Count == 0) return (e.Pos.Column, e.Pos.Column + Math.Max(1, e.CellWidth));
        int min = ports[0].BoundaryOffset, max = ports[0].BoundaryOffset;
        foreach (var p in ports) { min = Math.Min(min, p.BoundaryOffset); max = Math.Max(max, p.BoundaryOffset); }
        return (e.Pos.Column + min, e.Pos.Column + max);
    }

    /// <summary>ネットリストの電気要素(Component)を生成するか。false は記号のみ（非シミュレート）。</summary>
    public static bool CreatesComponent(ElementInstance e, PartLibrary? lib)
    {
        var part = lib?.Get(e.PartId);
        if (part is not null) return part.Role != PartRole.NonSimulated;
        return ElementCatalog.CreatesComponent(e.Kind);
    }

    /// <summary>Component に用いる種別。自作パーツは役割から組込み種別へ写像（Evaluator が評価できる形）。</summary>
    public static ElementKind ComponentKind(ElementInstance e, PartLibrary? lib)
    {
        var part = lib?.Get(e.PartId);
        if (part is null) return e.Kind;
        return part.Role switch
        {
            PartRole.ContactNO => ElementKind.ContactNO,
            PartRole.ContactNC => ElementKind.ContactNC,
            PartRole.Coil => ElementKind.Coil,
            PartRole.Lamp => ElementKind.Lamp,
            PartRole.Terminal => ElementKind.Terminal,
            PartRole.InputNO => ElementKind.PushButtonNO,
            PartRole.InputNC => ElementKind.PushButtonNC,
            _ => throw new InvalidOperationException(
                $"ComponentKind called for role '{part.Role}'. Check CreatesComponent before calling ComponentKind."),
        };
    }
}

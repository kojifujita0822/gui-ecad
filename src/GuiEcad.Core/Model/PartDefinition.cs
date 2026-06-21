using System.Text.Json.Serialization;

namespace GuiEcad.Model;

/// <summary>自作パーツの電気的役割（テストモード挙動）。NetlistBuilder で既定種別へ写像する。</summary>
public enum PartRole { ContactNO, ContactNC, Coil, Lamp, Terminal, NonSimulated }

/// <summary>
/// パーツ図形プリミティブ（パーツローカル座標＝セル単位。原点=最左ポート点・行中心線=y0、+x右/+y下）。
/// JSON 多態シリアライズ対応。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PartLine), "line")]
[JsonDerivedType(typeof(PartCircle), "circle")]
[JsonDerivedType(typeof(PartArc), "arc")]
[JsonDerivedType(typeof(PartRect), "rect")]
[JsonDerivedType(typeof(PartPolyline), "polyline")]
[JsonDerivedType(typeof(PartText), "text")]
public abstract record PartPrimitive;

public sealed record PartLine(double X1, double Y1, double X2, double Y2) : PartPrimitive;
public sealed record PartCircle(double Cx, double Cy, double R) : PartPrimitive;
public sealed record PartArc(double Cx, double Cy, double R, double StartDeg, double SweepDeg, double Ry = 0, double Rot = 0) : PartPrimitive
{
    /// <summary>縦半径。0 以下なら真円弧（=横半径 R）として扱う（旧データ後方互換）。</summary>
    [JsonIgnore] public double EffRy => Ry > 0 ? Ry : R;
}
public sealed record PartRect(double X, double Y, double W, double H, double Rot = 0) : PartPrimitive;   // Rot=中心まわり回転（度）
public sealed record PartPolyline(double[] Points) : PartPrimitive;   // x0,y0,x1,y1,...
public sealed record PartText(string Text, double X, double Y, double SizeCells = 0.25) : PartPrimitive;

/// <summary>
/// 自作パーツの定義。基準範囲(W×Hセル)の枠内に図形を描き、接続点(境界ノード)を持つ。
/// 接続点は既存の <see cref="PortDef"/>（境界ノード一致で結線）を再利用する。
/// </summary>
public sealed class PartDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>基準範囲（外形枠）の幅・高さ（セル単位）。</summary>
    public int WidthCells { get; set; } = 1;
    public int HeightCells { get; set; } = 1;
    public PartRole Role { get; set; } = PartRole.ContactNO;
    /// <summary>接続点。2端子役割は先頭=NetA・末尾=NetB（境界オフセット昇順を想定）。</summary>
    public List<PortDef> Ports { get; set; } = new();
    public List<PartPrimitive> Primitives { get; set; } = new();
}

/// <summary>自作パーツのライブラリ（Id→定義）。ドキュメント埋め込み／外部JSONの両方で持てる。</summary>
public sealed class PartLibrary
{
    public Dictionary<string, PartDefinition> ById { get; set; } = new();

    public PartDefinition? Get(string? id) => id is not null && ById.TryGetValue(id, out var d) ? d : null;
}

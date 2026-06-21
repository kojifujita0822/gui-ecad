namespace GuiEcad.Model;

/// <summary>プロジェクト全体（永続化のルート）。</summary>
public sealed class LadderDocument
{
    /// <summary>.GCADファイルのスキーマバージョン（現在=1）。GcadSerializer が設定・検証する。</summary>
    public int SchemaVersion { get; set; } = 1;
    public DocumentInfo Info { get; set; } = new();
    public DocumentSettings Settings { get; set; } = new();
    public DeviceTable Devices { get; set; } = new();
    /// <summary>ドキュメント埋め込みの自作パーツライブラリ（null = 組込み種別のみ）。</summary>
    public PartLibrary? Library { get; set; }
    public List<Sheet> Sheets { get; set; } = new();
}

/// <summary>タイトルブロック相当のメタ情報。</summary>
public sealed class DocumentInfo
{
    public string Title { get; set; } = "";
    public string DrawingNo { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Designer { get; set; } = "";
    public string Drafter { get; set; } = "";
    public string Checker { get; set; } = "";
    public string? Date { get; set; }
    /// <summary>改定履歴（古い順）。</summary>
    public List<RevisionEntry> Revisions { get; set; } = new();
}

/// <summary>図面改定履歴の1エントリ。</summary>
public sealed class RevisionEntry
{
    public string Rev { get; set; } = "";
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public string By { get; set; } = "";
}

public sealed class DocumentSettings
{
    public BusConfig DefaultBus { get; set; } = new();
    /// <summary>true のとき PDF 出力に A4 横の図面枠（外枠・改定欄）を描画する。</summary>
    public bool EnableBorder { get; set; } = false;
}

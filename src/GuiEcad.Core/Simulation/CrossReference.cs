using GuiEcad.Model;

namespace GuiEcad.Simulation;

/// <summary>図面上のある位置（ページ番号＋回路番号）への参照。</summary>
public readonly record struct CircuitRef(int PageNumber, int CircuitNumber)
{
    public override string ToString() => $"{PageNumber}-{CircuitNumber}";
}

/// <summary>1機器のクロスリファレンス情報。コイル所在と接点所在の一覧を保持する。</summary>
public sealed class CrossRefEntry
{
    public string DeviceName { get; init; } = "";
    /// <summary>コイル（負荷）が現れる箇所。</summary>
    public List<CircuitRef> Coils { get; } = new();
    /// <summary>接点が現れる箇所。</summary>
    public List<CircuitRef> Contacts { get; } = new();
    /// <summary>この機器に付いたコメント（重複除去。複数要素にあれば登場順に保持）。</summary>
    public List<string> Comments { get; } = new();
}

/// <summary>
/// ドキュメント全体のクロスリファレンス。
/// <see cref="CircuitNumberer.Number"/> でシートが採番済みであることが前提。
/// </summary>
public sealed class CrossReference
{
    private readonly Dictionary<string, CrossRefEntry> _entries = new();

    /// <summary>全エントリを機器名昇順で返す。</summary>
    public IEnumerable<CrossRefEntry> Entries =>
        _entries.Values.OrderBy(e => e.DeviceName, StringComparer.OrdinalIgnoreCase);

    /// <summary>コイルが存在するエントリのみ（一覧表の対象）を機器名昇順で返す。</summary>
    public IEnumerable<CrossRefEntry> CoilEntries =>
        Entries.Where(e => e.Coils.Count > 0);

    public bool TryGet(string deviceName, out CrossRefEntry entry) =>
        _entries.TryGetValue(deviceName, out entry!);

    internal CrossRefEntry GetOrAdd(string deviceName)
    {
        if (!_entries.TryGetValue(deviceName, out var entry))
            _entries[deviceName] = entry = new CrossRefEntry { DeviceName = deviceName };
        return entry;
    }
}

/// <summary>
/// LadderDocument からクロスリファレンスを生成する。
/// 各要素の回路番号は <see cref="Sheet.Lines"/> を参照する（<see cref="CircuitNumberer"/> 呼び出し後に使用）。
/// 回路番号未割り当ての行（Lines に存在しない）は CircuitNumber=0 で扱う。
/// </summary>
public static class CrossReferenceBuilder
{
    public static CrossReference Build(LadderDocument doc, PartLibrary? lib = null)
    {
        var xref = new CrossReference();

        foreach (var sheet in doc.Sheets.OrderBy(s => s.PageNumber))
        {
            foreach (var elem in sheet.Elements)
            {
                if (string.IsNullOrEmpty(elem.DeviceName)) continue;

                if (!PartResolver.CreatesComponent(elem, lib)) continue;
                var effectiveKind = PartResolver.ComponentKind(elem, lib);
                bool isLoad = ElementCatalog.IsLoad(effectiveKind);
                bool isContact = ElementCatalog.IsContact(effectiveKind);
                if (!isLoad && !isContact) continue;

                // 位置番号は図面左の行番号ガイド（全行・1始まり=行インデックス+1）に一致させる。
                // 回路番号（要素のある行だけの連番）は空行があると図面の行番号とずれるため使わない。
                var cref = new CircuitRef(sheet.PageNumber, elem.Pos.Row + 1);
                var entry = xref.GetOrAdd(elem.DeviceName);

                if (isLoad)
                    entry.Coils.Add(cref);
                else
                    entry.Contacts.Add(cref);

                // コメントを機器単位で集約（空・重複は除外）。表の「コメント」列に出す。
                if (!string.IsNullOrWhiteSpace(elem.Comment) && !entry.Comments.Contains(elem.Comment))
                    entry.Comments.Add(elem.Comment);
            }
        }

        return xref;
    }
}

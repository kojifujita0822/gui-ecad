using GuiEcad.Model;

namespace GuiEcad.Simulation;

/// <summary>設計ルール検査（DRC）の診断レベル。</summary>
public enum DiagnosticSeverity { Info, Warning, Error }

/// <summary>設計ルール検査（DRC）の1診断。<see cref="Locations"/> は該当箇所（ページ-回路番号）。</summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string DeviceName,
    string Message,
    IReadOnlyList<CircuitRef> Locations);

/// <summary>
/// 評価前の静的検査（Design Rule Check）。コア評価ロジックには非破壊。
/// P3: クロスリファレンス完全性（駆動元不明の接点／死にリレー）を診断する。
/// P2/P6: 接点種別と機器実体の整合（励磁系と入力系の混在）を診断する。
/// </summary>
public static class DesignRuleCheck
{
    /// <summary>接点はあるが駆動コイルが図面上に無い（駆動元不明）。</summary>
    public const string ContactWithoutCoil = "DRC-XREF-001";
    /// <summary>コイルはあるが接点が図面上に一つも無い（死にリレー）。</summary>
    public const string CoilWithoutContact = "DRC-XREF-002";
    /// <summary>1コイルに対しリレー接点が物理上限（4個）を超えている。</summary>
    public const string TooManyRelayContacts = "DRC-XREF-003";

    /// <summary>1リレー（コイル1個）が持てる接点の物理上限。これを超えると警告する。</summary>
    public const int MaxRelayContactsPerCoil = 4;
    /// <summary>同一機器名に励磁系接点（ContactNO/NC）と入力系接点（押釦・タイマ等）が混在（P6）。</summary>
    public const string TypeConflictEnergizedVsInput = "DRC-TYPE-001";
    /// <summary>コイルで駆動される機器の接点種別が入力系（押釦・タイマ等）になっている（P2）。</summary>
    public const string CoilContactKindMismatch = "DRC-TYPE-002";
    /// <summary>縦コネクタが中間行の横配線と電気的に非接続で交差している（P7: ドット無し交差）。</summary>
    public const string VerticalCrossing = "DRC-CONN-001";
    /// <summary>負荷の入力側が左母線から到達不可（P8: 左母線への配線なし）。</summary>
    public const string LoadNotReachableFromLeft = "DRC-LOAD-001";
    /// <summary>負荷の出力側が右母線から到達不可（P8: 右母線への配線なし）。</summary>
    public const string LoadNotReachableFromRight = "DRC-LOAD-002";
    /// <summary>2つ以上のコイル（負荷）が直列に接続されている（二重コイル）。</summary>
    public const string SeriesCoils = "DRC-LOAD-003";

    /// <summary>
    /// クロスリファレンス完全性チェック（P3）。
    /// リレー接点（外部入力駆動の押釦・セレクト・タイマ接点・非常停止・OL を除く）と
    /// リレーコイル（<see cref="ElementKind.Coil"/>。表示負荷の <see cref="ElementKind.Lamp"/> を除く）の
    /// 対応関係を機器名ごとに照合し、片側のみの機器を警告する。
    /// <see cref="CircuitNumberer"/> でシートが採番済みであることが前提（未採番行は回路番号0）。
    /// </summary>
    public static IReadOnlyList<Diagnostic> CheckCrossReference(LadderDocument doc, PartLibrary? lib = null)
    {
        var usage = new Dictionary<string, DeviceUsage>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in doc.Sheets.OrderBy(s => s.PageNumber))
        {
            // Row → CircuitNumber のルックアップ（CrossReferenceBuilder と同一規則）
            var circuitByRow = sheet.CircuitByRow();

            foreach (var elem in sheet.Elements)
            {
                if (string.IsNullOrEmpty(elem.DeviceName)) continue;

                var kind = PartResolver.ComponentKind(elem, lib);
                // リレー接点＝接点のうち外部入力駆動でないもの（ContactNO/NC・TimerContactNO/NC）
                bool isRelayContact = ElementCatalog.IsContact(kind) && !ElementCatalog.IsInputControlled(kind);
                // リレーコイル＝Coil または Timer（Lamp は接点を持たない表示負荷のため対象外）
                bool isRelayCoil = kind is ElementKind.Coil or ElementKind.Timer;
                if (!isRelayContact && !isRelayCoil) continue;

                int circuitNo = circuitByRow.GetValueOrDefault(elem.Pos.Row, 0);
                var cref = new CircuitRef(sheet.PageNumber, circuitNo);

                if (!usage.TryGetValue(elem.DeviceName, out var u))
                    usage[elem.DeviceName] = u = new DeviceUsage();

                if (isRelayContact) u.RelayContacts.Add(cref);
                if (isRelayCoil) u.Coils.Add(cref);
            }
        }

        var diagnostics = new List<Diagnostic>();
        foreach (var (name, u) in usage.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool hasContact = u.RelayContacts.Count > 0;
            bool hasCoil = u.Coils.Count > 0;

            if (hasContact && !hasCoil)
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, ContactWithoutCoil, name,
                    $"機器 {name}: リレー接点がありますが駆動コイルが図面上に見つかりません（駆動元不明）。",
                    u.RelayContacts));

            if (hasCoil && !hasContact)
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, CoilWithoutContact, name,
                    $"機器 {name}: コイルがありますが接点が図面上に一つもありません（死にリレー）。",
                    u.Coils));

            // コイル1個に対しリレー接点が物理上限（4個）を超過。
            // リレーは構造上 4 接点までのため、5 個以上は機器選定ミスの可能性が高い。
            // （コイルが複数ある＝直列等は接点許容数の前提が変わるので、ここでは 1 コイルの場合のみ判定）
            if (u.Coils.Count == 1 && u.RelayContacts.Count > MaxRelayContactsPerCoil)
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, TooManyRelayContacts, name,
                    $"機器 {name}: リレー接点が {u.RelayContacts.Count} 個あります（コイル1個）。" +
                    $"リレーの接点は最大 {MaxRelayContactsPerCoil} 個までです。",
                    u.RelayContacts));
        }
        return diagnostics;
    }

    /// <summary>
    /// 接点種別と機器実体の整合チェック（P2/P6）。
    /// 同一機器名に励磁系接点（ContactNO/NC）と入力系接点（押釦・タイマ等）が混在する場合、
    /// またはコイルで駆動される機器の接点種別が入力系になっている場合を診断する。
    /// </summary>
    public static IReadOnlyList<Diagnostic> CheckDeviceTypeConsistency(LadderDocument doc, PartLibrary? lib = null)
    {
        // device name → (energized-controlled refs, input-controlled refs, has coil)
        var info = new Dictionary<string, DeviceTypeInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in doc.Sheets.OrderBy(s => s.PageNumber))
        {
            var circuitByRow = sheet.CircuitByRow();

            foreach (var elem in sheet.Elements)
            {
                if (string.IsNullOrEmpty(elem.DeviceName)) continue;
                var kind = PartResolver.ComponentKind(elem, lib);
                int circuitNo = circuitByRow.GetValueOrDefault(elem.Pos.Row, 0);
                var cref = new CircuitRef(sheet.PageNumber, circuitNo);

                if (!info.TryGetValue(elem.DeviceName, out var di))
                    info[elem.DeviceName] = di = new DeviceTypeInfo();

                if (ElementCatalog.IsLoad(kind))
                    di.CoilRefs.Add(cref);
                else if (ElementCatalog.IsContact(kind))
                {
                    if (ElementCatalog.IsInputControlled(kind))
                        di.InputControlledRefs.Add(cref);
                    else
                        di.EnergizedControlledRefs.Add(cref);
                }
            }
        }

        var diagnostics = new List<Diagnostic>();
        foreach (var (name, di) in info.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool hasEnergized = di.EnergizedControlledRefs.Count > 0;
            bool hasInput = di.InputControlledRefs.Count > 0;
            bool hasCoil = di.CoilRefs.Count > 0;

            // P6: 同一機器名に励磁系と入力系の接点が混在
            if (hasEnergized && hasInput)
            {
                var locs = di.EnergizedControlledRefs.Concat(di.InputControlledRefs).ToList();
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, TypeConflictEnergizedVsInput, name,
                    $"機器 {name}: 励磁系接点（ContactNO/NC）と入力系接点（押釦・タイマ等）が混在しています。シミュレーションが正しく動作しません。",
                    locs));
            }

            // P2: コイルで駆動される機器の接点が入力系種別になっている
            if (hasCoil && hasInput && !hasEnergized)
            {
                var locs = di.InputControlledRefs.Concat(di.CoilRefs).ToList();
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, CoilContactKindMismatch, name,
                    $"機器 {name}: コイルがありますが接点の種別が入力系（押釦・タイマ等）です。ContactNO/NC を使用してください。",
                    locs));
            }
        }
        return diagnostics;
    }

    /// <summary>
    /// 縦コネクタ中間行スルー交差チェック（P7）。
    /// Netlist.VerticalCrossings（NetlistBuilder が検出済み）から診断を生成する。
    /// </summary>
    public static IReadOnlyList<Diagnostic> CheckVerticalCrossings(Sheet sheet, Netlist net)
    {
        if (net.VerticalCrossings.Count == 0) return Array.Empty<Diagnostic>();
        var circuitByRow = sheet.CircuitByRow();
        var diags = new List<Diagnostic>();
        foreach (var (row, col) in net.VerticalCrossings)
        {
            int circuitNo = circuitByRow.GetValueOrDefault(row, 0);
            diags.Add(new Diagnostic(DiagnosticSeverity.Warning, VerticalCrossing, "",
                $"縦コネクタ（列{col}）が {row + 1} 行目の横配線と非接続で交差しています（ドット無し交差）。",
                [new CircuitRef(sheet.PageNumber, circuitNo)]));
        }
        return diags;
    }

    /// <summary>
    /// 負荷の母線到達可能性チェック（P8）。
    /// 全接点を強制導通（静的トポロジー）で BFS し、負荷の左右端子が各母線に到達できるか確認する。
    /// </summary>
    public static IReadOnlyList<Diagnostic> CheckLoadReachability(Sheet sheet, Netlist net)
    {
        var fromLeft  = FloodContacts(net, net.LeftRailNet);
        var fromRight = FloodContacts(net, net.RightRailNet);
        var circuitByRow = sheet.CircuitByRow();

        // SourceElementId → 回路番号 のルックアップ
        var elemCircuit = sheet.Elements.ToDictionary(e => e.Id,
            e => circuitByRow.GetValueOrDefault(e.Pos.Row, 0));

        var diags = new List<Diagnostic>();
        foreach (var c in net.Components)
        {
            if (c.Role != ComponentRole.Load) continue;
            int circuitNo = elemCircuit.TryGetValue(c.SourceElementId, out var cn) ? cn : 0;
            var loc = new CircuitRef(sheet.PageNumber, circuitNo);
            string name = c.DeviceName ?? "";

            if (!fromLeft.Contains(c.NetA))
                diags.Add(new Diagnostic(DiagnosticSeverity.Error, LoadNotReachableFromLeft, name,
                    $"機器 {name}: 負荷の入力側が左母線から到達不可（左母線への配線なし）。",
                    [loc]));

            if (!fromRight.Contains(c.NetB))
                diags.Add(new Diagnostic(DiagnosticSeverity.Error, LoadNotReachableFromRight, name,
                    $"機器 {name}: 負荷の出力側が右母線から到達不可（右母線への配線なし）。",
                    [loc]));
        }
        return diags;
    }

    /// <summary>
    /// 二重コイル（コイル直列接続）チェック。
    /// 2つ以上の負荷が共有する節点が、いずれの母線からも接点経由で到達できない場合、
    /// その節点は負荷どうしの直列接続点＝二重コイルである（並列接続の共有節点は母線到達可能なので除外される）。
    /// </summary>
    public static IReadOnlyList<Diagnostic> CheckSeriesCoils(Sheet sheet, Netlist net)
    {
        var fromLeft = FloodContacts(net, net.LeftRailNet);
        var fromRight = FloodContacts(net, net.RightRailNet);
        var circuitByRow = sheet.CircuitByRow();
        var elemCircuit = sheet.Elements.ToDictionary(e => e.Id,
            e => circuitByRow.GetValueOrDefault(e.Pos.Row, 0));

        // 節点 → その節点に端子を持つ負荷の一覧
        var loadsByNet = new Dictionary<int, List<Component>>();
        foreach (var c in net.Components)
        {
            if (c.Role != ComponentRole.Load) continue;
            (loadsByNet.TryGetValue(c.NetA, out var la) ? la : loadsByNet[c.NetA] = new()).Add(c);
            (loadsByNet.TryGetValue(c.NetB, out var lb) ? lb : loadsByNet[c.NetB] = new()).Add(c);
        }

        var diags = new List<Diagnostic>();
        foreach (var (node, loads) in loadsByNet)
        {
            var distinct = loads.Distinct().ToList();
            if (distinct.Count < 2) continue;
            // 母線から接点だけで到達できる節点＝並列接続点（正常）。到達できない節点＝直列接続点。
            if (fromLeft.Contains(node) || fromRight.Contains(node)) continue;

            var names = string.Join(", ", distinct.Select(c => string.IsNullOrEmpty(c.DeviceName) ? "(無名)" : c.DeviceName));
            var locs = distinct
                .Select(c => new CircuitRef(sheet.PageNumber, elemCircuit.GetValueOrDefault(c.SourceElementId, 0)))
                .Distinct().ToList();
            diags.Add(new Diagnostic(DiagnosticSeverity.Warning, SeriesCoils, distinct[0].DeviceName ?? "",
                $"コイル {names} が直列に接続されています（二重コイル）。各コイルは単独で母線間に接続してください。",
                locs));
        }
        return diags;
    }

    /// <summary>全接点・パススルーを双方向導通扱いで BFS（負荷は通過しない）。静的配線到達可能性検査用。</summary>
    private static HashSet<int> FloodContacts(Netlist net, int startNet)
    {
        var visited = new HashSet<int> { startNet };
        var queue = new Queue<int>([startNet]);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var c in net.Components)
            {
                if (c.Role == ComponentRole.Load) continue;
                int? other = c.NetA == cur ? c.NetB : c.NetB == cur ? c.NetA : null;
                if (other.HasValue && visited.Add(other.Value))
                    queue.Enqueue(other.Value);
            }
        }
        return visited;
    }

    /// <summary>1機器のリレー接点／リレーコイル所在の集計。</summary>
    private sealed class DeviceUsage
    {
        public List<CircuitRef> RelayContacts { get; } = new();
        public List<CircuitRef> Coils { get; } = new();
    }

    private sealed class DeviceTypeInfo
    {
        public List<CircuitRef> EnergizedControlledRefs { get; } = new();
        public List<CircuitRef> InputControlledRefs { get; } = new();
        public List<CircuitRef> CoilRefs { get; } = new();
    }
}

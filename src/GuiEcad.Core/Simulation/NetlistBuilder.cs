using GuiEcad.Model;

namespace GuiEcad.Simulation;

/// <summary>
/// シートの幾何から電気ネットリストを構築する。
/// 接続モデル（Port／node-coincidence）: 各要素はカタログ定義の接続点（ポート）を持ち、
/// ポートは列境界ノード (Row, Boundary) に載る。同一ノードに載るポート同士が電気的に同一ネット。
/// - 母線: 左端子が境界0、右端子が境界 Columns に載れば各母線へ（座標一致で成立）。
/// - 横配線: 同一行で隣接する要素は、左要素の右ポートと右要素の左ポートを結ぶ（空セルを跨いで連続）。
/// - 縦コネクタ/分岐: 端点ノード (Row, Boundary) 同士を結合（旧 NodeAtColumn の近接推測は廃止）。
/// 接点・コイルは左右2ポート間でネットを分断する。端子台など通過接続要素
/// （ElementCatalog.IsPassthrough）は左右ポートを同一ネットに union し、電気的に連続にする
/// （入口/出口の線番も同一になる）。
/// </summary>
public static class NetlistBuilder
{
    public static Netlist Build(Sheet sheet, PartLibrary? parts = null)
    {
        var elements = sheet.Elements;
        int columns = sheet.Grid.Columns;

        // ノード割当: (Row, Boundary) → 連番インデックス。母線は番兵キーで専用ノード。
        var nodeIndex = new Dictionary<(int Row, int Boundary), int>();
        int Node(int row, int boundary)
        {
            var key = (row, boundary);
            if (!nodeIndex.TryGetValue(key, out var id)) { id = nodeIndex.Count; nodeIndex[key] = id; }
            return id;
        }
        int leftRail = Node(-1, -1);   // 番兵: 実セル行は 0 以上なので衝突しない
        int rightRail = Node(-1, -2);

        // ---- フェーズ1: 要素ポート解析 ----
        var (leftBoundary, rightBoundary, leftNode, rightNode, hasPorts, unions) =
            BuildElementConnections(elements, columns, parts, Node, leftRail, rightRail);

        // ---- フェーズ2: 行ごとに要素を集約し横配線 unions を追加 ----
        var byRow = BuildRowIndex(elements, hasPorts);
        foreach (var (row, _) in byRow) byRow[row].Sort((a, b) => elements[a].Pos.Column.CompareTo(elements[b].Pos.Column));
        AddHorizontalWireUnions(byRow, leftNode, rightNode, leftRail, unions);
        AddRightRailAutoConnections(byRow, elements, rightBoundary, rightNode, columns, parts, rightRail, unions);

        // 指定 (行, 境界) の配線が属するノードを返す（縦コネクタ端点の解決）。
        // ポートが厳密一致すればそれ。なければ母線端、あるいは同一行の横配線が覆う境界へ帰着。
        int ResolveNode(int row, double boundary)
        {
            // 整数境界は厳密一致を試す。0.5 位置（セル中央）は横配線セグメントへ帰着させる。
            if (boundary == Math.Floor(boundary) &&
                nodeIndex.TryGetValue((row, (int)boundary), out var exact)) return exact;
            if (boundary <= 0) return leftRail;
            if (boundary >= columns) return rightRail;

            if (byRow.TryGetValue(row, out var idxs))
            {
                int leftEl = -1, rightEl = -1;
                foreach (var i in idxs)
                {
                    if (rightBoundary[i] <= boundary && (leftEl == -1 || rightBoundary[i] > rightBoundary[leftEl])) leftEl = i;
                    if (leftBoundary[i] >= boundary && (rightEl == -1 || leftBoundary[i] < leftBoundary[rightEl])) rightEl = i;
                }
                if (leftEl != -1) return rightNode[leftEl];   // 左隣要素の右ポートへ（横配線で連続）
                if (rightEl != -1) return leftNode[rightEl];  // 右隣要素の左ポートへ
            }
            return Node(row, (int)Math.Round(boundary)); // どの配線にも載らない宙ぶらり端点
        }

        // ---- フェーズ3: 縦コネクタ ----
        var vcNodes = BuildVerticalConnectorUnions(sheet, ResolveNode, unions);

        // ---- フェーズ4: Union-Find 実行 → ネット割当 ----
        var uf = new UnionFind(nodeIndex.Count);
        foreach (var (a, b) in unions) uf.Union(a, b);

        var repToNet = new Dictionary<int, int>();
        int Net(int node)
        {
            int r = uf.Find(node);
            if (!repToNet.TryGetValue(r, out var id)) { id = repToNet.Count; repToNet[r] = id; }
            return id;
        }
        int leftRailNet = Net(leftRail);
        int rightRailNet = Net(rightRail);
        // 全ノードのネットIDを確定（Component を持たない非シミュレート要素の孤立ネットも含める）
        foreach (var node in nodeIndex.Values) Net(node);

        // P7: 縦コネクタ中間行スルー交差検出
        var crossings = DetectVerticalCrossings(vcNodes, nodeIndex, Net);

        // ---- フェーズ5: Component 化 ----
        var (components, timerSetpoints) = BuildComponents(elements, hasPorts, leftNode, rightNode, parts, Net);

        // ---- フェーズ6: Netlist 組立 ----
        var nets = BuildNets(repToNet, leftRailNet, rightRailNet, sheet);
        AssignWireNumbers(nets, nodeIndex, Net, leftRailNet, rightRailNet);

        return new Netlist
        {
            Nets = nets,
            Components = components,
            LeftRailNet = leftRailNet,
            RightRailNet = rightRailNet,
            TimerSetpoints = timerSetpoints,
            VerticalCrossings = crossings,
        };
    }

    // ---- フェーズ1ヘルパ: 要素ポート解析 ----
    private static (int[] leftBoundary, int[] rightBoundary, int[] leftNode, int[] rightNode,
        bool[] hasPorts, List<(int, int)> unions)
        BuildElementConnections(
            IReadOnlyList<ElementInstance> elements, int columns, PartLibrary? parts,
            Func<int, int, int> Node, int leftRail, int rightRail)
    {
        var leftBoundary = new int[elements.Count];
        var rightBoundary = new int[elements.Count];
        var leftNode = new int[elements.Count];
        var rightNode = new int[elements.Count];
        var hasPorts = new bool[elements.Count];
        var unions = new List<(int, int)>();

        for (int i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            var ports = PartResolver.Ports(e, parts);
            // ポート0個（接続点なし）の自作パーツは電気的に寄与しない。配列はデフォルトのまま残し、後段の配線・Component 化から除外する。
            if (ports.Count == 0) continue;
            hasPorts[i] = true;
            // 全ポートのノードを作成。中間ポート（多端子）も座標一致で自動結線される。
            foreach (var p in ports)
                Node(e.Pos.Row + p.RowOffset, e.Pos.Column + p.BoundaryOffset);

            // 最左ポート(=NetA)・最右ポート(=NetB)を境界オフセットで決める（順不同に対応）
            var pl = ports[0];
            var pr = ports[0];
            foreach (var p in ports)
            {
                if (p.BoundaryOffset < pl.BoundaryOffset) pl = p;
                if (p.BoundaryOffset > pr.BoundaryOffset) pr = p;
            }
            leftBoundary[i] = e.Pos.Column + pl.BoundaryOffset;
            rightBoundary[i] = e.Pos.Column + pr.BoundaryOffset;
            leftNode[i] = Node(e.Pos.Row + pl.RowOffset, leftBoundary[i]);
            rightNode[i] = Node(e.Pos.Row + pr.RowOffset, rightBoundary[i]);

            if (leftBoundary[i] == 0) unions.Add((leftNode[i], leftRail));
            if (rightBoundary[i] == columns) unions.Add((rightNode[i], rightRail));

            // 端子台など通過接続要素は左右ポートが電気的に連続（同一ノード）。
            // 左右を union して同一ネットにする → 線番も入口/出口で同一になる。
            if (PartResolver.CreatesComponent(e, parts) &&
                ElementCatalog.IsPassthrough(PartResolver.ComponentKind(e, parts)))
                unions.Add((leftNode[i], rightNode[i]));
        }

        return (leftBoundary, rightBoundary, leftNode, rightNode, hasPorts, unions);
    }

    // ---- フェーズ2ヘルパ: 行インデックス構築 ----
    private static Dictionary<int, List<int>> BuildRowIndex(IReadOnlyList<ElementInstance> elements, bool[] hasPorts)
    {
        var byRow = new Dictionary<int, List<int>>();
        for (int i = 0; i < elements.Count; i++)
        {
            if (!hasPorts[i]) continue;   // 接続点なしの要素は横配線・母線接続の対象外
            int row = elements[i].Pos.Row;
            if (!byRow.TryGetValue(row, out var list)) { list = new(); byRow[row] = list; }
            list.Add(i);
        }
        return byRow;
    }

    // ---- フェーズ2ヘルパ: 横配線 unions を追加 ----
    // 同一行・隣接要素間の横配線（空セルを跨いで連続）。
    // 最左要素の左ポートを左母線へ接続（左母線→最左要素間は常に横配線が繋がる）。
    private static void AddHorizontalWireUnions(
        Dictionary<int, List<int>> byRow, int[] leftNode, int[] rightNode,
        int leftRail, List<(int, int)> unions)
    {
        foreach (var idxs in byRow.Values)
        {
            unions.Add((leftNode[idxs[0]], leftRail));
            for (int k = 1; k < idxs.Count; k++)
                unions.Add((rightNode[idxs[k - 1]], leftNode[idxs[k]]));
        }
    }

    // ---- フェーズ2ヘルパ: 末尾負荷・末尾端子台の右母線自動接続 ----
    // 描画上は末尾要素の右から右母線まで横線が延びるため電気的にも繋がるべき。
    private static void AddRightRailAutoConnections(
        Dictionary<int, List<int>> byRow, IReadOnlyList<ElementInstance> elements,
        int[] rightBoundary, int[] rightNode, int columns, PartLibrary? parts,
        int rightRail, List<(int, int)> unions)
    {
        foreach (var idxs in byRow.Values)
        {
            int last = idxs[^1];
            if (rightBoundary[last] < columns && PartResolver.CreatesComponent(elements[last], parts))
            {
                var kind = PartResolver.ComponentKind(elements[last], parts);
                if (ElementCatalog.IsLoad(kind) || ElementCatalog.IsPassthrough(kind))
                    unions.Add((rightNode[last], rightRail));
            }
        }
    }

    // ---- フェーズ3ヘルパ: 縦コネクタ unions を追加 ----
    // 縦コネクタ（分岐）: 端点ノードを結合。交差検出用に topNode を保持。
    private static List<(VerticalConnector Vc, int TopNode)> BuildVerticalConnectorUnions(
        Sheet sheet, Func<int, double, int> resolveNode, List<(int, int)> unions)
    {
        var vcNodes = new List<(VerticalConnector Vc, int TopNode)>(sheet.Connectors.Count);
        foreach (var c in sheet.Connectors)
        {
            int topNode = resolveNode(c.TopRow, c.Column);
            int botNode = resolveNode(c.BottomRow, c.Column);
            unions.Add((topNode, botNode));
            vcNodes.Add((c, topNode));
        }
        return vcNodes;
    }

    // ---- フェーズ4ヘルパ: 縦コネクタ中間行スルー交差検出 (P7) ----
    private static List<(int Row, int Col)> DetectVerticalCrossings(
        List<(VerticalConnector Vc, int TopNode)> vcNodes,
        Dictionary<(int Row, int Boundary), int> nodeIndex,
        Func<int, int> netOf)
    {
        var crossings = new List<(int Row, int Col)>();
        foreach (var (vc, topNode) in vcNodes)
        {
            int vcNet = netOf(topNode);
            bool integral = vc.Column == Math.Floor(vc.Column);   // 0.5 位置は中間行ノードと厳密一致しない
            for (int r = vc.TopRow + 1; r < vc.BottomRow; r++)
            {
                if (integral && nodeIndex.TryGetValue((r, (int)vc.Column), out var midNode) && netOf(midNode) != vcNet)
                    crossings.Add((r, (int)vc.Column));
            }
        }
        return crossings;
    }

    // ---- フェーズ5ヘルパ: Component 化 ----
    private static (List<Component> components, Dictionary<string, double> timerSetpoints)
        BuildComponents(
            IReadOnlyList<ElementInstance> elements, bool[] hasPorts,
            int[] leftNode, int[] rightNode, PartLibrary? parts, Func<int, int> Net)
    {
        var components = new List<Component>(elements.Count);
        var timerSetpoints = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            if (!hasPorts[i]) continue;   // 接続点なし＝ネット未定義。Component 化しない
            if (!PartResolver.CreatesComponent(e, parts)) continue;   // 記号のみ(三相モータ・非シミュ自作)は評価対象外
            var kind = PartResolver.ComponentKind(e, parts);
            var role = ElementCatalog.IsLoad(kind) ? ComponentRole.Load
                     : ElementCatalog.IsPassthrough(kind) ? ComponentRole.Passthrough
                     : ComponentRole.Contact;
            int switchPos = 0;
            if (kind == ElementKind.SelectSwitch &&
                e.Params.TryGetValue("Position", out var ps)) int.TryParse(ps, out switchPos);
            // タイマコイル: 設定時間（秒）を Params["Setpoint"] から読む
            if (kind == ElementKind.Timer && !string.IsNullOrEmpty(e.DeviceName) &&
                e.Params.TryGetValue("Setpoint", out var sp) &&
                double.TryParse(sp, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double setpoint))
            {
                timerSetpoints[e.DeviceName] = setpoint;
            }
            components.Add(new Component
            {
                Kind = kind,
                DeviceName = e.DeviceName,
                NetA = Net(leftNode[i]),
                NetB = Net(rightNode[i]),
                Role = role,
                SwitchPosition = switchPos,
                SourceElementId = e.Id,
            });
        }

        return (components, timerSetpoints);
    }

    // ---- フェーズ6ヘルパ: Net リスト組立 ----
    private static List<Net> BuildNets(
        Dictionary<int, int> repToNet, int leftRailNet, int rightRailNet, Sheet sheet)
    {
        var nets = new List<Net>(repToNet.Count);
        for (int id = 0; id < repToNet.Count; id++)
        {
            bool isRail = id == leftRailNet || id == rightRailNet;
            string? name = id == leftRailNet ? sheet.Bus.LeftName
                         : id == rightRailNet ? sheet.Bus.RightName
                         : null;
            nets.Add(new Net { Id = id, WireNumber = 0, IsRail = isRail, Name = name });
        }
        return nets;
    }

    // 線番を読み順で採番（仕様: docs/drawing-spec.md「線番採番ルーチン」）。
    // 母線ネットは番号でなく名前（除外）。内部ネットを代表座標 (最小 Row, 最小 Boundary) で
    // ソートし 1..N を付与。回路 上→下／行内 主線 左→右 → 分岐枝（主線が上の行のため自然に成立）。
    private static void AssignWireNumbers(
        List<Net> nets, Dictionary<(int Row, int Boundary), int> nodeIndex,
        Func<int, int> netOf, int leftRailNet, int rightRailNet)
    {
        var minCoord = new Dictionary<int, (int Row, int Boundary)>();
        foreach (var kv in nodeIndex)
        {
            if (kv.Key.Row < 0) continue;            // 母線の番兵座標は除外
            int net = netOf(kv.Value);
            if (net == leftRailNet || net == rightRailNet) continue;
            if (!minCoord.TryGetValue(net, out var cur) ||
                kv.Key.Row < cur.Row || (kv.Key.Row == cur.Row && kv.Key.Boundary < cur.Boundary))
                minCoord[net] = kv.Key;
        }

        var ordered = minCoord.Keys.ToList();
        ordered.Sort((a, b) =>
        {
            var (ca, cb) = (minCoord[a], minCoord[b]);
            int c = ca.Row.CompareTo(cb.Row);
            if (c != 0) return c;
            c = ca.Boundary.CompareTo(cb.Boundary);
            return c != 0 ? c : a.CompareTo(b);
        });

        int wire = 1;
        foreach (var net in ordered) nets[net].WireNumber = wire++;
    }
}

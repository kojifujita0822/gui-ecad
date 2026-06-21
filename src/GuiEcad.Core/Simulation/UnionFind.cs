namespace GuiEcad.Simulation;

internal sealed class UnionFind
{
    private readonly int[] _parent;

    public UnionFind(int n)
    {
        _parent = new int[n];
        for (int i = 0; i < n; i++) _parent[i] = i;
    }

    public int Find(int x)
    {
        while (_parent[x] != x)
        {
            _parent[x] = _parent[_parent[x]]; // 経路圧縮
            x = _parent[x];
        }
        return x;
    }

    public void Union(int a, int b)
    {
        int ra = Find(a), rb = Find(b);
        if (ra != rb) _parent[ra] = rb;
    }
}

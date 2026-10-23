namespace GitManager.Core;

public sealed record GraphEdge(int FromLane, double FromY, int ToLane, double ToY, int Color);
public sealed record GraphRow(int NodeLane, int Color, int LaneCount, bool IsHead, IReadOnlyList<GraphEdge> Edges);
public sealed record GraphLayoutResult(IReadOnlyList<GraphRow> Rows, int LaneCount);

public static class GraphLayout
{
    private sealed record Lane(string Target, int Color);
    private sealed record LogicalEdge(int FromColor, double FromY, int ToColor, double ToY, int Color);
    private sealed record LogicalRow(int Color, bool IsHead, IReadOnlyList<LogicalEdge> Edges);
    private sealed class Lineage(int color, DateTimeOffset date, int start)
    {
        public int Color { get; } = color;
        public DateTimeOffset Date { get; } = date;
        public int Start { get; } = start;
        public int End { get; set; } = start;
        public int Column { get; set; }
    }

    // Plan against the complete lightweight topology, then expose only the requested page.
    // A lineage owns one column until it ends, so discovering another branch cannot bend it.
    public static GraphLayoutResult Build(IReadOnlyList<CommitInfo> commits, int? rowLimit = null, CancellationToken ct = default)
    {
        var active = new List<Lane>();
        var outputCount = Math.Min(commits.Count, Math.Max(0, rowLimit ?? commits.Count));
        var logical = new List<LogicalRow>(outputCount);
        var lineages = new List<Lineage>();
        var nextColor = 0;
        Lane CreateLane(string target, DateTimeOffset date, int row, int? color = null)
        {
            if (color is null) lineages.Add(new Lineage(nextColor, date, row));
            return new Lane(target, color ?? nextColor++);
        }
        for (var rowIndex = 0; rowIndex < commits.Count; rowIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var commit = commits[rowIndex];
            var before = active.ToArray();
            // Several lineages can be waiting for the same ancestor. Keep their colors
            // separate until that ancestor, where the oldest lineage owns the node.
            var nodeLane = active.Select((lane, index) => (lane, index)).Where(pair => pair.lane.Target == commit.Id)
                .OrderBy(pair => pair.lane.Color).Select(pair => pair.index).DefaultIfEmpty(-1).First();
            if (nodeLane < 0)
            {
                nodeLane = active.Count;
                active.Add(CreateLane(commit.Id, commit.Date, rowIndex));
            }
            var nodeColor = active[nodeLane].Color;
            active.RemoveAll(lane => lane.Target == commit.Id);
            var parentLanes = new List<Lane>();
            for (var p = 0; p < commit.Parents.Count; p++)
            {
                var parent = commit.Parents[p];
                var lane = active.FirstOrDefault(lane => lane.Target == parent && (p > 0 || lane.Color == nodeColor));
                if (lane is null)
                {
                    lane = CreateLane(parent, commit.Date, rowIndex, p == 0 ? nodeColor : null);
                    active.Add(lane);
                }
                parentLanes.Add(lane);
            }
            var edges = new List<LogicalEdge>();
            foreach (var lane in before)
            {
                if (lane.Target == commit.Id) edges.Add(new LogicalEdge(lane.Color, 0, nodeColor, .5, lane.Color));
                else edges.Add(new LogicalEdge(lane.Color, 0, lane.Color, 1, lane.Color));
            }
            foreach (var parentLane in parentLanes)
                edges.Add(new LogicalEdge(nodeColor, .5, parentLane.Color, 1, parentLane.Color));
            lineages[nodeColor].End = rowIndex;
            foreach (var edge in edges)
            {
                lineages[edge.FromColor].End = rowIndex;
                lineages[edge.ToColor].End = rowIndex;
            }
            if (rowIndex < outputCount)
                logical.Add(new LogicalRow(nodeColor,
                    commit.Decorations.Split(", ").Any(decoration => decoration == "HEAD" || decoration.StartsWith("HEAD -> ", StringComparison.Ordinal)), edges));
        }

        // Allocate older overlapping side lineages first. Non-overlapping branches
        // can reuse a column; newer overlapping ones remain to their right.
        var occupied = new ColumnOccupancy(commits.Count);
        foreach (var lineage in lineages.Skip(1).OrderBy(item => item.Date).ThenByDescending(item => item.Color))
        {
            ct.ThrowIfCancellationRequested();
            lineage.Column = occupied.Maximum(lineage.Start, lineage.End) + 1;
            occupied.Reserve(lineage.Start, lineage.End, lineage.Column);
        }
        var rows = new List<GraphRow>(outputCount);
        var maxLanes = 1;
        foreach (var row in logical)
        {
            ct.ThrowIfCancellationRequested();
            var edges = row.Edges.Select(edge => new GraphEdge(lineages[edge.FromColor].Column, edge.FromY,
                lineages[edge.ToColor].Column, edge.ToY, edge.Color)).ToArray();
            var nodeLane = lineages[row.Color].Column;
            var count = edges.SelectMany(edge => new[] { edge.FromLane, edge.ToLane }).Append(nodeLane).Max() + 1;
            maxLanes = Math.Max(maxLanes, count);
            rows.Add(new GraphRow(nodeLane, row.Color, count, row.IsHead, edges));
        }
        return new GraphLayoutResult(rows, maxLanes);
    }

    // Range maximum with lazy max updates keeps column planning O((rows + branches) log rows).
    private sealed class ColumnOccupancy(int count)
    {
        private readonly int[] _maximum = new int[Math.Max(1, count * 4)];
        private readonly int[] _reserved = new int[Math.Max(1, count * 4)];
        public int Maximum(int from, int to) => Query(1, 0, count - 1, from, to);
        public void Reserve(int from, int to, int column) => Update(1, 0, count - 1, from, to, column);
        private int Query(int node, int left, int right, int from, int to)
        {
            if (from <= left && right <= to) return _maximum[node];
            var middle = left + (right - left) / 2;
            var value = _reserved[node];
            if (from <= middle) value = Math.Max(value, Query(node * 2, left, middle, from, to));
            if (to > middle) value = Math.Max(value, Query(node * 2 + 1, middle + 1, right, from, to));
            return value;
        }
        private void Update(int node, int left, int right, int from, int to, int column)
        {
            _maximum[node] = Math.Max(_maximum[node], column);
            if (from <= left && right <= to) { _reserved[node] = Math.Max(_reserved[node], column); return; }
            var middle = left + (right - left) / 2;
            if (from <= middle) Update(node * 2, left, middle, from, to, column);
            if (to > middle) Update(node * 2 + 1, middle + 1, right, from, to, column);
        }
    }
}

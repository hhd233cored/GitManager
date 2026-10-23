namespace GitManager.Core;

public sealed record GraphEdge(int FromLane, double FromY, int ToLane, double ToY, int Color);
public sealed record GraphRow(int NodeLane, int Color, int LaneCount, bool IsHead, IReadOnlyList<GraphEdge> Edges);
public sealed record GraphLayoutResult(IReadOnlyList<GraphRow> Rows, int LaneCount);

public static class GraphLayout
{
    private sealed record Lane(string Target, int Color);

    // Active lanes are unresolved parent IDs. Row boundaries share the exact same lane state.
    public static GraphLayoutResult Build(IReadOnlyList<CommitInfo> commits)
    {
        var active = new List<Lane>();
        var rows = new List<GraphRow>(commits.Count);
        var nextColor = 0;
        var maxLanes = 1;
        foreach (var commit in commits)
        {
            var before = active.ToArray();
            var nodeLane = active.FindIndex(lane => lane.Target == commit.Id);
            if (nodeLane < 0)
            {
                nodeLane = active.Count;
                active.Add(new Lane(commit.Id, nextColor++));
            }
            var nodeColor = active[nodeLane].Color;
            active.RemoveAt(nodeLane);
            for (var p = 0; p < commit.Parents.Count; p++)
            {
                var parent = commit.Parents[p];
                if (active.Any(lane => lane.Target == parent)) continue;
                var insertion = Math.Min(nodeLane + p, active.Count);
                active.Insert(insertion, new Lane(parent, p == 0 ? nodeColor : nextColor++));
            }
            var edges = new List<GraphEdge>();
            for (var i = 0; i < before.Length; i++)
            {
                if (before[i].Target == commit.Id) edges.Add(new GraphEdge(i, 0, nodeLane, .5, before[i].Color));
                else
                {
                    var destination = active.FindIndex(lane => lane.Target == before[i].Target);
                    if (destination >= 0) edges.Add(new GraphEdge(i, 0, destination, 1, before[i].Color));
                }
            }
            foreach (var parent in commit.Parents)
            {
                var destination = active.FindIndex(lane => lane.Target == parent);
                edges.Add(new GraphEdge(nodeLane, .5, destination, 1, active[destination].Color));
            }
            var count = Math.Max(nodeLane + 1, Math.Max(before.Length, active.Count));
            maxLanes = Math.Max(maxLanes, count);
            rows.Add(new GraphRow(nodeLane, nodeColor, count,
                commit.Decorations.Split(", ").Any(decoration => decoration == "HEAD" || decoration.StartsWith("HEAD -> ", StringComparison.Ordinal)), edges));
        }
        return new GraphLayoutResult(rows, maxLanes);
    }
}

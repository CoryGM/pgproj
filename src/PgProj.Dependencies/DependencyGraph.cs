using PgProj.Semantics;

namespace PgProj.Dependencies;

public sealed class DependencyGraph
{
    private readonly Dictionary<string, DependencyNode> _nodes = new(PgProj.Parser.SqlIdentifiers.NameComparer);
    private readonly List<DependencyEdge> _edges = [];
    private readonly HashSet<(string FromNodeId, string ToNodeId)> _edgeSet = [];
    private readonly Dictionary<string, HashSet<string>> _dependenciesByNode = new(PgProj.Parser.SqlIdentifiers.NameComparer);
    private readonly Dictionary<string, HashSet<string>> _dependentsByNode = new(PgProj.Parser.SqlIdentifiers.NameComparer);
    private readonly Dictionary<string, HashSet<string>> _retiredByNode = new(PgProj.Parser.SqlIdentifiers.NameComparer);
    private readonly Dictionary<string, HashSet<string>> _retiredDependentsByNode = new(PgProj.Parser.SqlIdentifiers.NameComparer);

    public IReadOnlyDictionary<string, DependencyNode> Nodes => _nodes;

    /// <summary>
    /// The ids of the nodes of the model being deployed (the project), as opposed to those only in the model being replaced (the live
    /// database). Empty for a graph of one model, where every node is the target's (G-116).
    /// </summary>
    public IReadOnlySet<string> TargetNodeIds { get; internal set; } = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The replaced (live) model's privileges on objects when it leaves grants out (G-87), for re-granting a recreated object (G-116).</summary>
    public IReadOnlyList<UnmodeledPrivilege> ReplacedUnmodeledPrivileges { get; internal set; } = [];

    /// <summary>The replaced (live) model's view dependencies (G-103).</summary>
    public IReadOnlyList<LiveDependency> ReplacedDependencies { get; internal set; } = [];

    /// <summary>The replaced (live) model's view owners, by key (G-103).</summary>
    public IReadOnlyDictionary<string, string> ReplacedOwners { get; internal set; } = new Dictionary<string, string>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>
    /// The replaced (live) model's comments on the objects that are dropped and created again to change them (constraints, G-161; triggers, G-163; policies, G-164), by <see cref="CommentKey"/>: what such an object keeps when the
    /// project does not state a comment.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReplacedComments { get; internal set; } = new Dictionary<string, string>(PgProj.Parser.SqlIdentifiers.NameComparer);

    /// <summary>The key of a node in <see cref="ReplacedComments"/>: its kind and key, since a constraint and a trigger of one table may share a name.</summary>
    public static string CommentKey(ISemanticNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return $"{node.Kind}:{node.Key}";
    }

    /// <summary>The role the replaced (live) model was read as: the role a recreated object will be owned by (G-103).</summary>
    public string? ReplacedConnectedRole { get; internal set; }

    /// <summary>True when <paramref name="nodeId"/> is a node of the model being deployed.</summary>
    public bool IsTargetNode(string nodeId) => TargetNodeIds.Count == 0 ? _nodes.ContainsKey(nodeId) : TargetNodeIds.Contains(nodeId);

    /// <summary>
    /// Records that <paramref name="fromNodeId"/>, an object present in both models, used to depend on
    /// <paramref name="toNodeId"/> and no longer does (G-52). The main graph keeps only the dependencies of an object's new
    /// definition, so without this nothing says that a column retyped away from a removed type, or a default that stopped
    /// calling a removed function, has to change before the removed object can be dropped. These edges are deliberately kept
    /// out of <see cref="Edges"/>, <see cref="TopologicalSort"/>, and <see cref="DetectCycles"/>: they constrain only when a
    /// removed object may be dropped, never how objects are created.
    /// </summary>
    public void AddRetiredDependency(string fromNodeId, string toNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toNodeId);

        if (!_retiredByNode.TryGetValue(fromNodeId, out var retired))
        {
            _retiredByNode[fromNodeId] = retired = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        }

        retired.Add(toNodeId);

        if (!_retiredDependentsByNode.TryGetValue(toNodeId, out var dependents))
        {
            _retiredDependentsByNode[toNodeId] = dependents = new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer);
        }

        dependents.Add(fromNodeId);
    }

    /// <summary>The ids of the objects that used to depend on <paramref name="nodeId"/> and no longer do.</summary>
    public IReadOnlyCollection<string> GetRetiredDependents(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        return _retiredDependentsByNode.TryGetValue(nodeId, out var dependents) ? dependents.ToArray() : [];
    }

    /// <summary>The ids of the objects <paramref name="nodeId"/> used to depend on and no longer does.</summary>
    public IReadOnlyCollection<string> GetRetiredDependencies(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        return _retiredByNode.TryGetValue(nodeId, out var retired) ? retired.ToArray() : [];
    }

    public IReadOnlyList<DependencyEdge> Edges => _edges;

    /// <summary>
    /// The views whose cycle with other views was broken (G-254), with the views they refer to that were taken out of the way: each is created from its first definition and replaced by its real
    /// body once those views exist.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SplitViews { get; internal set; } = new Dictionary<string, IReadOnlyList<string>>(PgProj.Parser.SqlIdentifiers.NameComparer);

    internal bool RemoveEdge(string fromNodeId, string toNodeId)
    {
        if (!_edgeSet.Remove((fromNodeId, toNodeId)))
        {
            return false;
        }

        _edges.RemoveAll(edge => string.Equals(edge.FromNodeId, fromNodeId, StringComparison.Ordinal) && string.Equals(edge.ToNodeId, toNodeId, StringComparison.Ordinal));
        _dependenciesByNode[fromNodeId].Remove(toNodeId);
        _dependentsByNode[toNodeId].Remove(fromNodeId);
        return true;
    }

    public void AddNode(DependencyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        _nodes[node.NodeId] = node;
        _dependenciesByNode.TryAdd(node.NodeId, new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer));
        _dependentsByNode.TryAdd(node.NodeId, new HashSet<string>(PgProj.Parser.SqlIdentifiers.NameComparer));
    }

    public void AddEdge(string fromNodeId, string toNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toNodeId);

        if (!_nodes.ContainsKey(fromNodeId))
        {
            throw new KeyNotFoundException($"Unknown dependency graph node '{fromNodeId}'.");
        }

        if (!_nodes.ContainsKey(toNodeId))
        {
            throw new KeyNotFoundException($"Unknown dependency graph node '{toNodeId}'.");
        }

        if (!_edgeSet.Add((fromNodeId, toNodeId)))
        {
            return;
        }

        _edges.Add(new DependencyEdge
        {
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
        });

        _dependenciesByNode[fromNodeId].Add(toNodeId);
        _dependentsByNode[toNodeId].Add(fromNodeId);
    }

    public IReadOnlyList<DependencyNode> GetDependencies(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        return _dependenciesByNode.TryGetValue(nodeId, out var dependencyIds)
            ? dependencyIds.Where(_nodes.ContainsKey)
                .Select(dependencyId => _nodes[dependencyId])
                .ToArray()
            : [];
    }

    public IReadOnlyList<DependencyNode> GetDependents(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        return _dependentsByNode.TryGetValue(nodeId, out var dependentIds)
            ? dependentIds.Where(_nodes.ContainsKey)
                .Select(dependentId => _nodes[dependentId])
                .ToArray()
            : [];
    }

    public IReadOnlyList<DependencyNode> TopologicalSort()
    {
        var sorted = new List<DependencyNode>(_nodes.Count);
        var stateByNode = new Dictionary<string, VisitState>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var path = new Stack<string>();

        foreach (var nodeId in _nodes.Keys)
        {
            Visit(nodeId, sorted, stateByNode, path);
        }

        return sorted;
    }

    public IReadOnlyList<IReadOnlyList<string>> DetectCycles()
    {
        var stateByNode = new Dictionary<string, VisitState>(PgProj.Parser.SqlIdentifiers.NameComparer);
        var path = new Stack<string>();
        var cycles = new List<IReadOnlyList<string>>();

        foreach (var nodeId in _nodes.Keys)
        {
            Detect(nodeId, stateByNode, path, cycles);
        }

        return cycles;
    }

    private void Visit(
        string nodeId,
        ICollection<DependencyNode> sorted,
        IDictionary<string, VisitState> stateByNode,
        Stack<string> path)
    {
        if (stateByNode.TryGetValue(nodeId, out var state))
        {
            if (state == VisitState.Visited)
            {
                return;
            }

            if (state == VisitState.Visiting)
            {
                throw new GraphCycleException(CreateCyclePath(path, nodeId));
            }
        }

        stateByNode[nodeId] = VisitState.Visiting;
        path.Push(nodeId);

        if (_dependenciesByNode.TryGetValue(nodeId, out var dependencyIds))
        {
            foreach (var dependencyId in dependencyIds)
            {
                Visit(dependencyId, sorted, stateByNode, path);
            }
        }

        path.Pop();
        stateByNode[nodeId] = VisitState.Visited;
        sorted.Add(_nodes[nodeId]);
    }

    private void Detect(
        string nodeId,
        IDictionary<string, VisitState> stateByNode,
        Stack<string> path,
        ICollection<IReadOnlyList<string>> cycles)
    {
        if (stateByNode.TryGetValue(nodeId, out var state))
        {
            if (state == VisitState.Visited)
            {
                return;
            }

            if (state == VisitState.Visiting)
            {
                cycles.Add(CreateCyclePath(path, nodeId));
            }

            return;
        }

        stateByNode[nodeId] = VisitState.Visiting;
        path.Push(nodeId);

        if (_dependenciesByNode.TryGetValue(nodeId, out var dependencyIds))
        {
            foreach (var dependencyId in dependencyIds)
            {
                Detect(dependencyId, stateByNode, path, cycles);
            }
        }

        path.Pop();
        stateByNode[nodeId] = VisitState.Visited;
    }

    private static IReadOnlyList<string> CreateCyclePath(Stack<string> path, string repeatedNodeId)
    {
        var cycle = path.Reverse().SkipWhile(nodeId => !string.Equals(nodeId, repeatedNodeId, StringComparison.Ordinal)).ToList();
        cycle.Add(repeatedNodeId);
        return cycle;
    }

    private enum VisitState
    {
        Visiting,
        Visited,
    }
}

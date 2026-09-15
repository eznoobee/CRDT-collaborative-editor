using System.Text;

namespace Crdt.Core;

/// <summary>
/// One FugueMax replica of a text document.
/// </summary>
/// <remarks>
/// <para>
/// Implements the algorithm in PROJECT_SPEC.md §5 — Weidner and Kleppmann's
/// FugueMax, TPDS Algorithm 1 as amended by Definition 6. The document is a
/// tree; the visible text is its depth-first in-order traversal with tombstones
/// skipped.
/// </para>
/// <para>
/// The unit of text is a Unicode code point, never a UTF-16 code unit and never
/// a grapheme cluster (§7). Nothing here normalises: normalisation would change
/// element identity.
/// </para>
/// <para>
/// Traversal-order queries walk the whole tree. That is honest for Phase 1 and
/// wrong for Phase 7's targets; the subtree-size index that fixes it is an
/// optimisation, not a semantic change, and belongs with the load test that
/// justifies it.
/// </para>
/// </remarks>
public sealed class Replica
{
    private sealed class Node
    {
        public ElementId Id;
        public Rune Value;
        public bool IsDeleted;
        public bool IsRoot;
        public Node? Parent;
        public Side Side;

        /// <summary>
        /// Meaningful only when <see cref="Side"/> is <see cref="Side.Right"/>,
        /// where <see langword="null"/> means end-of-document — the paper's
        /// <c>end</c>. Left children do not carry one.
        /// </summary>
        public Node? RightOrigin;

        public List<Node> LeftChildren = [];
        public List<Node> RightChildren = [];
    }

    private readonly Node _root = new() { IsRoot = true, IsDeleted = true };
    private readonly Dictionary<ElementId, Node> _byId = [];
    private readonly Dictionary<ReplicaId, ulong> _versionVector = [];
    private readonly List<Operation> _log = [];
    private readonly List<Operation> _pending = [];
    private ulong _nextSeq;

    /// <summary>Creates an empty replica.</summary>
    public Replica(ReplicaId id) => Id = id;

    /// <summary>This replica's identifier.</summary>
    public ReplicaId Id { get; }

    /// <summary>The visible text: the traversal with tombstones skipped.</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var node in InOrder())
            {
                if (!node.IsDeleted)
                {
                    sb.Append(node.Value.ToString());
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>The visible text as code points.</summary>
    public IReadOnlyList<Rune> Values =>
        [.. InOrder().Where(n => !n.IsDeleted).Select(n => n.Value)];

    /// <summary>
    /// Element ids of the visible text, positionally aligned with
    /// <see cref="Values"/>.
    /// </summary>
    /// <remarks>
    /// Production API, not a test hook: §9 requires cursors to be anchored to
    /// element ids rather than integer indices, because an index is invalidated
    /// by any concurrent edit earlier in the document.
    /// </remarks>
    public IReadOnlyList<ElementId> VisibleIds =>
        [.. InOrder().Where(n => !n.IsDeleted).Select(n => n.Id)];

    /// <summary>
    /// Element ids in traversal order <em>including tombstones</em>.
    /// </summary>
    /// <remarks>
    /// This is the order the algorithm's right origins are defined against
    /// (Algorithm 1 line 24, and arXiv §5.1: "the right origin is a tombstone if
    /// the list element immediately following the left origin in the list is a
    /// tombstone"). It is the linearised output of the structure, not a window
    /// into it — no parent, side or sibling ordering is exposed.
    /// </remarks>
    public IReadOnlyList<ElementId> AllIds => [.. InOrder().Select(n => n.Id)];

    /// <summary>
    /// How many operations from each replica have been applied here.
    /// </summary>
    /// <remarks>
    /// The count is also the next <see cref="ElementId.Seq"/> expected from that
    /// replica, since sequences are dense from 0 (§5). Counting rather than
    /// storing a high water mark avoids the ambiguity at zero, where "0" would
    /// otherwise mean both "one operation seen" and "none".
    /// </remarks>
    public IReadOnlyDictionary<ReplicaId, ulong> VersionVector => _versionVector;

    /// <summary>
    /// Operations buffered because a dependency has not arrived (§5). A healthy
    /// replica drains this to zero once delivery catches up.
    /// </summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// How many operations may wait in the pending set (§5).
    /// </summary>
    /// <remarks>
    /// Unbounded by default, and that is deliberate: §5 bounds the pending set
    /// <em>per connection</em>, and a replica is not a connection. A replica
    /// replaying a stored trace or importing a snapshot legitimately buffers as
    /// much as the trace demands, and a core that refused would break the
    /// property suite for a reason that has nothing to do with the property.
    /// <para>
    /// Whoever attaches a replica to a network connection sets this, because
    /// that is the layer where the bound means something — an unbounded pending
    /// set fed by a remote peer is a denial-of-service vector, and one fed by a
    /// local file is not.
    /// </para>
    /// </remarks>
    public int MaxPending { get; set; } = int.MaxValue;

    /// <summary>
    /// Operations discarded because this replica had already applied them.
    /// </summary>
    /// <remarks>
    /// Diagnostic, not a health check. §5 guarantees this is non-zero in normal
    /// operation; what is worth alerting on is its rate.
    /// </remarks>
    public long DuplicatesDropped { get; private set; }

    /// <summary>
    /// Inserts <paramref name="value"/> at <paramref name="index"/> in the
    /// visible text, applying it locally and returning the operation to broadcast.
    /// </summary>
    public InsertOperation Insert(int index, Rune value)
    {
        var all = InOrder();
        var visible = all.Where(n => !n.IsDeleted).ToArray();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, visible.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var leftOrigin = index == 0 ? _root : visible[index - 1];

        // Algorithm 1 line 24: the next node in the traversal that includes
        // tombstones. Computed once, before the placement branch.
        var rightOrigin = NextIncludingTombstones(all, leftOrigin);

        var id = new ElementId(Id, _nextSeq);
        InsertOperation op;

        if (leftOrigin.RightChildren.Count == 0)
        {
            // Right child of the left origin, tagged with its right origin
            // (Algorithm 1 lines 25-26, Definition 6 change 1).
            op = new InsertOperation(
                id,
                value,
                leftOrigin.IsRoot ? null : leftOrigin.Id,
                Side.Right,
                rightOrigin?.Id);
        }
        else
        {
            // Left child of the right origin (Algorithm 1 lines 27-28). The
            // right origin cannot be null here: a node with right children
            // always has a successor in the traversal.
            var parent = rightOrigin
                ?? throw new InvalidOperationException(
                    "A node with right children must have a traversal successor.");

            op = new InsertOperation(id, value, parent.Id, Side.Left, null);
        }

        Apply(op);
        return op;
    }

    /// <summary>
    /// Tombstones the element at <paramref name="index"/> in the visible text,
    /// applying it locally and returning the operation to broadcast.
    /// </summary>
    public DeleteOperation Delete(int index)
    {
        var visible = InOrder().Where(n => !n.IsDeleted).ToArray();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, visible.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var op = new DeleteOperation(new ElementId(Id, _nextSeq), visible[index].Id);
        Apply(op);
        return op;
    }

    /// <summary>
    /// Applies an operation, buffering it if a dependency is missing.
    /// </summary>
    /// <remarks>
    /// Idempotent: an operation already seen is a no-op (invariant 2). An insert
    /// depends on its parent and, when it is a right child with a non-null right
    /// origin, on that too — two dependencies, not one.
    /// </remarks>
    public void Apply(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (HasSeen(operation))
        {
            // Counted, not merely skipped (§5). Duplicate delivery is
            // guaranteed — the backplane can repeat a broadcast, catch-up
            // re-sends what a client already has, a client dropped for
            // backpressure recovers by being resent state — so the number is
            // never zero and is not itself a problem. A sudden rise in it is
            // how a resend loop announces itself, and that signal does not
            // exist if duplicates are silently absorbed.
            DuplicatesDropped++;
            return;
        }

        if (!IsReady(operation))
        {
            if (_pending.Any(p => p.Id.Equals(operation.Id)))
            {
                // Already buffered. This is the duplicate the watermark cannot
                // see, because the operation has not been applied yet (§5), and
                // buffering it twice would apply it twice when the gap closes.
                DuplicatesDropped++;
            }
            else
            {
                if (_pending.Count >= MaxPending)
                {
                    // §5: exceeding the bound is a protocol violation, not
                    // something to absorb by dropping the oldest. Dropping
                    // would leave this replica permanently missing an operation
                    // with nothing to indicate it, which is divergence arrived
                    // at quietly — the one outcome this project exists to
                    // prevent. Throwing hands the decision to the connection
                    // layer, which can close and resync.
                    throw new PendingSetOverflowException(_pending.Count, MaxPending);
                }

                _pending.Add(operation);
            }

            return;
        }

        ApplyReady(operation);
        DrainPending();
    }

    /// <summary>
    /// Operations this replica knows that a peer at <paramref name="remote"/>
    /// does not, in an order safe to apply.
    /// </summary>
    public IReadOnlyList<Operation> OperationsSince(
        IReadOnlyDictionary<ReplicaId, ulong> remote)
    {
        ArgumentNullException.ThrowIfNull(remote);

        return
        [
            .. _log.Where(op =>
                op.Id.Seq >= (remote.TryGetValue(op.Id.Replica, out var seen) ? seen : 0UL)),
        ];
    }

    /// <summary>
    /// The document's elements in traversal order, tombstones included.
    /// </summary>
    /// <remarks>
    /// The basis of a snapshot (§6). Tombstones are included because later
    /// operations still attach to them: dropping them would make the snapshot
    /// unable to accept operations that a full replay accepts.
    /// </remarks>
    public IReadOnlyList<ElementState> Export() =>
    [
        .. InOrder().Select(n => new ElementState(
            n.Id,
            n.Value,
            n.Parent is { IsRoot: false } parent ? parent.Id : null,
            n.Side,
            n.RightOrigin?.Id,
            n.IsDeleted)),
    ];

    /// <summary>
    /// Rebuilds a replica from exported elements and a version vector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Elements are placed with the same sibling ordering as a live insert
    /// rather than trusting the order they arrive in, so a snapshot that was
    /// written wrongly produces a different tree here instead of quietly
    /// restoring a corrupt one.
    /// </para>
    /// <para>
    /// Traversal order does not guarantee parents precede children — a left
    /// child is traversed before its parent — so placement iterates until no
    /// further element can be attached.
    /// </para>
    /// </remarks>
    public static Replica Import(
        ReplicaId id,
        IReadOnlyList<ElementState> elements,
        IReadOnlyDictionary<ReplicaId, ulong> versionVector)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(versionVector);

        var replica = new Replica(id);
        var remaining = elements.ToList();

        // Each pass rebuilds the unplaced list rather than removing from it.
        // Removing by index is linear, which would make importing a large
        // snapshot quadratic in the common case where everything places on the
        // first pass.
        while (remaining.Count > 0)
        {
            var deferred = new List<ElementState>();

            foreach (var element in remaining)
            {
                var parentPresent = element.Parent is null || replica._byId.ContainsKey(element.Parent.Value);
                var originPresent = element.RightOrigin is null
                                    || replica._byId.ContainsKey(element.RightOrigin.Value);

                if (!parentPresent || !originPresent)
                {
                    deferred.Add(element);
                    continue;
                }

                var parent = element.Parent is { } parentId ? replica._byId[parentId] : replica._root;
                var node = new Node
                {
                    Id = element.Id,
                    Value = element.Value,
                    IsDeleted = element.IsDeleted,
                    Parent = parent,
                    Side = element.Side,
                    RightOrigin = element.RightOrigin is { } originId ? replica._byId[originId] : null,
                };

                replica._byId[node.Id] = node;
                InsertAmongSiblings(node, parent);
            }

            if (deferred.Count == remaining.Count)
            {
                throw new InvalidOperationException(
                    $"{deferred.Count} elements reference a parent or right origin that is not in the "
                    + "snapshot. The snapshot is incomplete or was written out of order.");
            }

            remaining = deferred;
        }

        foreach (var (replicaId, count) in versionVector)
        {
            replica._versionVector[replicaId] = count;
            if (replicaId.Equals(id))
            {
                replica._nextSeq = count;
            }
        }

        return replica;
    }

    /// <summary>
    /// Reclaims tombstones that every replica in <paramref name="stableFrontier"/>
    /// has observed, returning how many were collected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Causal stability (§5): an element is collectable only when every
    /// non-retired replica has seen it, so no future legal operation can
    /// reference it. <paramref name="stableFrontier"/> is the elementwise
    /// minimum of the known replicas' version vectors.
    /// </para>
    /// <para>
    /// A tombstone still referenced as a live node's parent or right origin
    /// cannot be removed outright; it keeps its place as a structural
    /// placeholder. Correctness first — a lower reclamation rate is fine, a
    /// broken tree is not.
    /// </para>
    /// </remarks>
    public int Collect(IReadOnlyDictionary<ReplicaId, ulong> stableFrontier)
    {
        ArgumentNullException.ThrowIfNull(stableFrontier);

        var collected = 0;
        bool progressed;

        // Iterated to a fixpoint. Forward typing builds a chain of right
        // children, so each element in a deleted run is its successor's parent
        // and only the tail is a leaf. One pass would collect that tail and
        // stop; repeating collapses the run to its leading tombstone, which is
        // what §5 says this rule does.
        do
        {
            progressed = false;
            var referenced = ReferencedAsRightOrigin();
            var order = InOrder();

            for (var i = 0; i < order.Count; i++)
            {
                var node = order[i];

                if (!node.IsDeleted
                    || node.LeftChildren.Count > 0
                    || node.RightChildren.Count > 0
                    || referenced.Contains(node)
                    || !IsCausallyStable(node, stableFrontier))
                {
                    continue;
                }

                // Retain the first tombstone of every run of consecutive
                // tombstones. A future insert names as its right origin the
                // first node after a visible left origin, so only that leading
                // tombstone is reachable; the ones behind it can never be named
                // again. Collecting the leader would promote the next tombstone
                // into reachable position, so it stays. See §5.
                if (i == 0 || !order[i - 1].IsDeleted)
                {
                    continue;
                }

                var siblings = node.Side == Side.Left
                    ? node.Parent!.LeftChildren
                    : node.Parent!.RightChildren;

                siblings.Remove(node);
                _byId.Remove(node.Id);
                collected++;
                progressed = true;
            }
        }
        while (progressed);

        return collected;
    }

    private static bool IsCausallyStable(
        Node node, IReadOnlyDictionary<ReplicaId, ulong> frontier) =>
        frontier.TryGetValue(node.Id.Replica, out var seen) && node.Id.Seq < seen;

    private HashSet<Node> ReferencedAsRightOrigin()
    {
        var referenced = new HashSet<Node>();
        foreach (var node in InOrder())
        {
            if (node.RightOrigin is { } origin)
            {
                referenced.Add(origin);
            }
        }

        return referenced;
    }

    private bool HasSeen(Operation operation) =>
        _versionVector.TryGetValue(operation.Id.Replica, out var count)
        && count > operation.Id.Seq;

    private bool IsReady(Operation operation)
    {
        // Sequences are dense, so an operation that skips one cannot be applied
        // yet even if its structural dependencies are present.
        var expected = _versionVector.TryGetValue(operation.Id.Replica, out var c) ? c : 0UL;
        if (operation.Id.Seq != expected)
        {
            return false;
        }

        return operation switch
        {
            InsertOperation insert =>
                (insert.Parent is null || _byId.ContainsKey(insert.Parent.Value))
                && (insert.Side != Side.Right
                    || insert.RightOrigin is null
                    || _byId.ContainsKey(insert.RightOrigin.Value)),
            DeleteOperation delete => _byId.ContainsKey(delete.Target),
            _ => throw new InvalidOperationException($"Unknown operation {operation}."),
        };
    }

    private void ApplyReady(Operation operation)
    {
        switch (operation)
        {
            case InsertOperation insert:
                ApplyInsert(insert);
                break;
            case DeleteOperation delete:
                _byId[delete.Target].IsDeleted = true;
                break;
            default:
                throw new InvalidOperationException($"Unknown operation {operation}.");
        }

        _versionVector[operation.Id.Replica] = operation.Id.Seq + 1;
        _log.Add(operation);

        if (operation.Id.Replica.Equals(Id))
        {
            _nextSeq = operation.Id.Seq + 1;
        }
    }

    private void ApplyInsert(InsertOperation insert)
    {
        var parent = insert.Parent is null ? _root : _byId[insert.Parent.Value];
        var node = new Node
        {
            Id = insert.Id,
            Value = insert.Value,
            Parent = parent,
            Side = insert.Side,
            RightOrigin = insert.Side == Side.Right && insert.RightOrigin is { } origin
                ? _byId[origin]
                : null,
        };

        _byId[node.Id] = node;
        InsertAmongSiblings(node, parent);
    }

    /// <summary>Definition 6, and Algorithm 1 lines 32-37.</summary>
    private static void InsertAmongSiblings(Node node, Node parent)
    {
        if (node.Side == Side.Left)
        {
            // Left siblings are ordered by ascending id.
            var siblings = parent.LeftChildren;
            var i = 0;
            while (i < siblings.Count && node.Id.CompareTo(siblings[i].Id) >= 0)
            {
                i++;
            }

            siblings.Insert(i, node);
        }
        else
        {
            // Right siblings are ordered by their right origins in reverse list
            // order, ties broken by ascending id.
            var siblings = parent.RightChildren;
            var i = 0;
            while (i < siblings.Count)
            {
                var sibling = siblings[i];
                var byOrigin = ComparePosition(node.RightOrigin, sibling.RightOrigin);

                var nodeComesFirst = byOrigin > 0
                    || (byOrigin == 0 && node.Id.CompareTo(sibling.Id) < 0);

                if (nodeComesFirst)
                {
                    break;
                }

                i++;
            }

            siblings.Insert(i, node);
        }
    }

    /// <summary>
    /// Compares two nodes by their position in the traversal, treating
    /// <see langword="null"/> as end-of-document.
    /// </summary>
    private static int ComparePosition(Node? a, Node? b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        // End of document sorts after every real node.
        if (a is null)
        {
            return 1;
        }

        if (b is null)
        {
            return -1;
        }

        var x = a;
        var y = b;
        var depthX = Depth(x);
        var depthY = Depth(y);

        // If one is an ancestor of the other, the descendant's position is
        // decided by which side of the ancestor it hangs from.
        while (depthX > depthY)
        {
            var side = x.Side;
            x = x.Parent!;
            depthX--;
            if (ReferenceEquals(x, b))
            {
                return side == Side.Left ? -1 : 1;
            }
        }

        while (depthY > depthX)
        {
            var side = y.Side;
            y = y.Parent!;
            depthY--;
            if (ReferenceEquals(y, a))
            {
                return side == Side.Left ? 1 : -1;
            }
        }

        // Same depth and neither contains the other: rise to the common parent.
        while (!ReferenceEquals(x.Parent, y.Parent))
        {
            x = x.Parent!;
            y = y.Parent!;
        }

        if (x.Side != y.Side)
        {
            return x.Side == Side.Left ? -1 : 1;
        }

        var siblings = x.Side == Side.Left ? x.Parent!.LeftChildren : x.Parent!.RightChildren;
        return siblings.IndexOf(x).CompareTo(siblings.IndexOf(y));
    }

    private static int Depth(Node node)
    {
        var depth = 0;
        for (var current = node; current.Parent is not null; current = current.Parent)
        {
            depth++;
        }

        return depth;
    }

    private void DrainPending()
    {
        bool progressed;
        do
        {
            progressed = false;
            for (var i = 0; i < _pending.Count; i++)
            {
                var candidate = _pending[i];
                if (HasSeen(candidate))
                {
                    _pending.RemoveAt(i--);
                    progressed = true;
                }
                else if (IsReady(candidate))
                {
                    _pending.RemoveAt(i--);
                    ApplyReady(candidate);
                    progressed = true;
                }
            }
        }
        while (progressed);
    }

    /// <summary>Depth-first in-order traversal, tombstones included.</summary>
    /// <remarks>
    /// Iterative, not recursive. Typing left to right makes each character a
    /// right child of the previous one, so a document's tree depth equals its
    /// length — a recursive walk overflows the stack on a document of the size
    /// §8 targets, and takes the process with it. Found by the snapshot size
    /// metric, which is why that measurement exists.
    /// </remarks>
    private List<Node> InOrder()
    {
        var result = new List<Node>();

        // Each frame is (node, phase, next child index): phase 0 walks the left
        // children, 1 emits the node, 2 walks the right children.
        var stack = new Stack<(Node Node, int Phase, int Index)>();
        stack.Push((_root, 0, 0));

        while (stack.Count > 0)
        {
            var (node, phase, index) = stack.Pop();

            switch (phase)
            {
                case 0 when index < node.LeftChildren.Count:
                    stack.Push((node, 0, index + 1));
                    stack.Push((node.LeftChildren[index], 0, 0));
                    break;

                case 0:
                    stack.Push((node, 1, 0));
                    break;

                case 1:
                    if (!node.IsRoot)
                    {
                        result.Add(node);
                    }

                    stack.Push((node, 2, 0));
                    break;

                default:
                    if (index < node.RightChildren.Count)
                    {
                        stack.Push((node, 2, index + 1));
                        stack.Push((node.RightChildren[index], 0, 0));
                    }

                    break;
            }
        }

        return result;
    }

    private static Node? NextIncludingTombstones(List<Node> all, Node leftOrigin)
    {
        if (leftOrigin.IsRoot)
        {
            return all.Count > 0 ? all[0] : null;
        }

        var index = all.IndexOf(leftOrigin);
        return index >= 0 && index + 1 < all.Count ? all[index + 1] : null;
    }
}

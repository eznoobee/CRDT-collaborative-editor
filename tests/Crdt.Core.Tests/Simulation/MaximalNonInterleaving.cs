using Crdt.Core;

namespace Crdt.Core.Tests.Simulation;

/// <summary>
/// Evaluates TPDS Definition 4 against a finished simulation.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is derived from the paper's definitions and from what the
/// authoring replica saw at insert time — never from the CRDT's internal tree.
/// That matters: PROJECT_SPEC.md §5 warns that a misreading of the algorithm
/// would agree with itself, so a check that consulted the implementation's own
/// structure would confirm whatever the implementation happens to do.
/// </para>
/// <para>
/// The left-origin tree used by the Lemma 5 exception is rebuilt here from
/// recorded intentions, independently of any tree the implementation keeps.
/// </para>
/// </remarks>
public sealed class MaximalNonInterleaving
{
    private readonly Dictionary<ElementId, int> _position = [];
    private readonly Dictionary<ElementId, ElementId?> _leftOrigin = [];
    private readonly Dictionary<ElementId, ElementId?> _rightOrigin = [];
    private readonly int _count;

    private MaximalNonInterleaving(SimulationResult result)
    {
        var visible = result.Replicas[0].VisibleIds;
        _count = visible.Count;
        for (var i = 0; i < visible.Count; i++)
        {
            _position[visible[i]] = i;
        }

        foreach (var intention in result.Intentions)
        {
            _leftOrigin[intention.Inserted] = intention.Left;
            _rightOrigin[intention.Inserted] = intention.Right;
        }
    }

    /// <summary>Analyses the converged state of a simulation.</summary>
    public static MaximalNonInterleaving Analyse(SimulationResult result) => new(result);

    /// <summary>Position of an element, or -1 for start and the length for end.</summary>
    private bool TryPosition(ElementId? id, bool isRightSide, out int position)
    {
        if (id is null)
        {
            position = isRightSide ? _count : -1;
            return true;
        }

        return _position.TryGetValue(id.Value, out position);
    }

    private bool IsDescendantOfOrEqual(ElementId candidate, ElementId? ancestor)
    {
        if (ancestor is null)
        {
            // Everything descends from the start sentinel.
            return true;
        }

        var current = (ElementId?)candidate;
        var guard = 0;
        while (current is not null && guard++ < 10_000)
        {
            if (current.Value.Equals(ancestor.Value))
            {
                return true;
            }

            current = _leftOrigin.TryGetValue(current.Value, out var parent) ? parent : null;
        }

        return false;
    }

    /// <summary>
    /// Condition (1): forward non-interleaving. Unconditional — it holds for any
    /// number of replicas.
    /// </summary>
    public IReadOnlyList<string> ForwardViolations()
    {
        var violations = new List<string>();

        foreach (var group in _position.Keys
                     .Where(_leftOrigin.ContainsKey)
                     .GroupBy(id => _leftOrigin[id]))
        {
            if (!TryPosition(group.Key, isRightSide: false, out var originPosition))
            {
                continue; // origin was deleted; its position is not defined
            }

            var earliest = group.MinBy(id => _position[id]);
            if (_position[earliest] != originPosition + 1)
            {
                violations.Add(
                    $"forward: element at {_position[earliest]} has left origin at "
                    + $"{originPosition} and is the earliest such element, so they must be "
                    + "consecutive (TPDS Def. 4 condition 1)");
            }
        }

        return violations;
    }

    /// <summary>
    /// Condition (2): backward non-interleaving, excluding the Lemma 5 cases
    /// where forward non-interleaving forces a conflict.
    /// </summary>
    public IReadOnlyList<string> BackwardViolations()
    {
        var violations = new List<string>();

        foreach (var group in _position.Keys
                     .Where(_rightOrigin.ContainsKey)
                     .GroupBy(id => _rightOrigin[id]))
        {
            if (!TryPosition(group.Key, isRightSide: true, out var originPosition))
            {
                continue;
            }

            var latest = group.MaxBy(id => _position[id]);
            if (_position[latest] == originPosition - 1)
            {
                continue;
            }

            if (Lemma5ExceptionApplies(latest, group.Key, originPosition))
            {
                continue;
            }

            violations.Add(
                $"backward: element at {_position[latest]} has right origin at "
                + $"{originPosition} and is the latest such element, so they must be "
                + "consecutive unless the Lemma 5 exception applies (TPDS Def. 4 condition 2)");
        }

        return violations;
    }

    /// <summary>
    /// TPDS Lemma 5: A and B need not be consecutive when they have different
    /// left origins and some C sits between A's left origin and B without
    /// descending from A's left origin.
    /// </summary>
    private bool Lemma5ExceptionApplies(ElementId a, ElementId? b, int bPosition)
    {
        var aLeft = _leftOrigin.GetValueOrDefault(a);
        var bLeft = b is null ? null : _leftOrigin.GetValueOrDefault(b.Value);

        if (Nullable.Equals(aLeft, bLeft))
        {
            return false; // condition (i) fails: same left origins
        }

        if (!TryPosition(aLeft, isRightSide: false, out var aLeftPosition))
        {
            return false;
        }

        foreach (var (candidate, position) in _position)
        {
            if (position > aLeftPosition
                && position < bPosition
                && !IsDescendantOfOrEqual(candidate, aLeft))
            {
                return true; // condition (ii) holds
            }
        }

        return false;
    }

    /// <summary>
    /// Condition (3): elements sharing both origins are ordered by ascending id.
    /// </summary>
    public IReadOnlyList<string> SameOriginViolations()
    {
        var violations = new List<string>();

        var candidates = _position.Keys
            .Where(id => _leftOrigin.ContainsKey(id) && _rightOrigin.ContainsKey(id))
            .ToArray();

        foreach (var a in candidates)
        {
            foreach (var b in candidates)
            {
                if (a.Equals(b)
                    || !Nullable.Equals(_leftOrigin[a], _leftOrigin[b])
                    || !Nullable.Equals(_rightOrigin[a], _rightOrigin[b]))
                {
                    continue;
                }

                if (a.CompareTo(b) < 0 && _position[a] > _position[b])
                {
                    violations.Add(
                        "same-origins: the lower id must appear earlier "
                        + "(TPDS Def. 4 condition 3)");
                }
            }
        }

        return violations;
    }
}

namespace Crdt.Core;

/// <summary>Which child list of its parent a node belongs to.</summary>
/// <remarks>
/// Left children are traversed before the parent's own value, right children
/// after (PROJECT_SPEC.md §5). The two sides use different sibling orderings,
/// which is where FugueMax differs from base Fugue.
/// </remarks>
public enum Side
{
    /// <summary>Traversed before the parent's value; ordered by ascending id.</summary>
    Left,

    /// <summary>
    /// Traversed after the parent's value; ordered by reverse list order of
    /// right origin, then by ascending id.
    /// </summary>
    Right,
}

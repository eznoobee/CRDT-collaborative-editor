using System.Text;

namespace Crdt.Core;

/// <summary>One element of a document, as it is stored or transported.</summary>
/// <remarks>
/// <para>
/// A snapshot cannot be the visible text: text is not resumable. Operations
/// arriving after a snapshot attach to elements by id, including to tombstones,
/// so the structure has to survive — every field FugueMax needs to place a node
/// is here (PROJECT_SPEC.md §5).
/// </para>
/// <para>
/// This is plain state, not serialisation. Turning it into rows or JSON belongs
/// to <c>Editor.Infrastructure</c> and to the TypeScript client, because
/// <c>Crdt.Core</c> references nothing but the BCL (§4).
/// </para>
/// </remarks>
public readonly record struct ElementState(
    ElementId Id,
    Rune Value,
    ElementId? Parent,
    Side Side,
    ElementId? RightOrigin,
    bool IsDeleted);

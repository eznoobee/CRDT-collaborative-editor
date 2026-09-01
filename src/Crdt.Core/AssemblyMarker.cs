namespace Crdt.Core;

/// <summary>
/// Anchor for reflection over this assembly. Exists so tests can reference
/// <c>Crdt.Core</c> without depending on algorithm types that do not exist yet.
/// </summary>
/// <remarks>
/// PROJECT_SPEC.md §4 requires this assembly to reference nothing but the BCL.
/// <c>ArchitectureTests</c> enforces that by inspecting this assembly's
/// references, so this type must stay in the root namespace of the assembly.
/// </remarks>
public static class AssemblyMarker { }

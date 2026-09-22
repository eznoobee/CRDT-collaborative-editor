namespace Editor.Domain;

/// <summary>A member's role on a document (PROJECT_SPEC.md §7).</summary>
public enum Role
{
    /// <summary>Receives broadcasts; any write is rejected and logged.</summary>
    Viewer = 0,

    /// <summary>May submit operations.</summary>
    Editor = 1,

    /// <summary>May submit operations and manage membership.</summary>
    Owner = 2,
}

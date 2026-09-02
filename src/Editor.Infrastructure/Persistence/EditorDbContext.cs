using Editor.Domain;
using Microsoft.EntityFrameworkCore;

namespace Editor.Infrastructure.Persistence;

/// <summary>The schema of PROJECT_SPEC.md §6.</summary>
/// <remarks>
/// EF Core owns the schema and non-hot-path queries (§3). The hot path —
/// receive, validate, persist, broadcast — does not come through here; §8
/// forbids loading full document state on it.
/// </remarks>
public sealed class EditorDbContext(DbContextOptions<EditorDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentMember> DocumentMembers => Set<DocumentMember>();

    public DbSet<DocumentReplica> DocumentReplicas => Set<DocumentReplica>();

    public DbSet<DocumentOperationRow> DocumentOperations => Set<DocumentOperationRow>();

    public DbSet<DocumentSnapshotRow> DocumentSnapshots => Set<DocumentSnapshotRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OidcIssuer).HasColumnName("oidc_issuer").HasMaxLength(512);
            entity.Property(e => e.OidcSubject).HasColumnName("oidc_subject").HasMaxLength(512);
            entity.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(256);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            // An OIDC subject is unique per issuer, not globally (§6).
            entity.HasIndex(e => new { e.OidcIssuer, e.OidcSubject }).IsUnique();
        });

        modelBuilder.Entity<Document>(entity =>
        {
            entity.ToTable("documents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OwnerId).HasColumnName("owner_id");
            entity.Property(e => e.Title).HasColumnName("title").HasMaxLength(512);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        });

        modelBuilder.Entity<DocumentMember>(entity =>
        {
            entity.ToTable("document_members");
            entity.HasKey(e => new { e.DocumentId, e.UserId });
            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.Role).HasColumnName("role").HasConversion<int>();
            entity.Property(e => e.GrantedAt).HasColumnName("granted_at");
            entity.Property(e => e.GrantedBy).HasColumnName("granted_by");
        });

        modelBuilder.Entity<DocumentReplica>(entity =>
        {
            entity.ToTable("document_replicas");
            entity.HasKey(e => new { e.DocumentId, e.ReplicaId });
            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.ReplicaId).HasColumnName("replica_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(e => e.OperationCount).HasColumnName("operation_count");
            entity.Property(e => e.RetiredAt).HasColumnName("retired_at");
        });

        modelBuilder.Entity<DocumentOperationRow>(entity =>
        {
            entity.ToTable("document_ops");

            // Duplicate submission is a no-op at the database, which is the
            // cheapest correct place to enforce idempotency (§6). document_id
            // leads because it is also the partition key, which Postgres
            // requires to be part of the key.
            entity.HasKey(e => new { e.DocumentId, e.ReplicaId, e.Seq });

            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.ReplicaId).HasColumnName("replica_id");
            entity.Property(e => e.Seq).HasColumnName("seq");
            entity.Property(e => e.OpType).HasColumnName("op_type").HasMaxLength(16);
            entity.Property(e => e.ParentReplica).HasColumnName("parent_replica");
            entity.Property(e => e.ParentSeq).HasColumnName("parent_seq");
            entity.Property(e => e.Side).HasColumnName("side").HasMaxLength(1);
            entity.Property(e => e.RightOriginReplica).HasColumnName("right_origin_replica");
            entity.Property(e => e.RightOriginSeq).HasColumnName("right_origin_seq");
            entity.Property(e => e.RightOriginIsEnd).HasColumnName("right_origin_is_end");
            entity.Property(e => e.Value).HasColumnName("value").HasMaxLength(8);
            entity.Property(e => e.TargetReplica).HasColumnName("target_replica");
            entity.Property(e => e.TargetSeq).HasColumnName("target_seq");
            entity.Property(e => e.ServerSeq).HasColumnName("server_seq");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            // Catch-up reads by server_seq; the primary key does not serve it.
            entity.HasIndex(e => new { e.DocumentId, e.ServerSeq });
        });

        modelBuilder.Entity<DocumentSnapshotRow>(entity =>
        {
            entity.ToTable("document_snapshots");
            entity.HasKey(e => new { e.DocumentId, e.ServerSeq });
            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.ServerSeq).HasColumnName("server_seq");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.VersionVector).HasColumnName("version_vector");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        });
    }
}

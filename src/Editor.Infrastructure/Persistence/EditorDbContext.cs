using System.Text.Json;
using Editor.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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

    /// <summary>
    /// Serialises a version vector to jsonb by name, rather than opting the
    /// whole data source into dynamic JSON.
    /// </summary>
    /// <remarks>
    /// Npgsql refuses to write an arbitrary <c>Dictionary</c> to a jsonb
    /// parameter unless <c>EnableDynamicJson</c> is called, and that call is a
    /// global opt-in: it turns on reflection-based serialisation for every type
    /// this data source ever writes, not for the two columns that need it.
    /// §13.29's rule — name the specific thing you are trusting, never widen
    /// the class — makes this the narrower choice, and it costs one converter.
    /// </remarks>
    private static readonly ValueConverter<Dictionary<Guid, long>, string> VersionVectorConverter =
        new(
            vector => JsonSerializer.Serialize(vector, VersionVectorJson),
            json => JsonSerializer.Deserialize<Dictionary<Guid, long>>(json, VersionVectorJson)
                ?? new Dictionary<Guid, long>());

    /// <summary>
    /// Compares and clones version vectors by value.
    /// </summary>
    /// <remarks>
    /// A converted mutable reference type is compared by reference unless it is
    /// told otherwise, so EF would miss every in-place change to a vector and
    /// silently save nothing. The snapshot clone matters for the same reason:
    /// without it the "original" value is the same object as the current one,
    /// and nothing ever looks modified.
    /// </remarks>
    private static readonly ValueComparer<Dictionary<Guid, long>> VersionVectorComparer =
        new(
            (left, right) => left != null && right != null
                ? left.Count == right.Count && !left.Except(right).Any()
                : left == right,
            vector => vector.Aggregate(
                0,
                (hash, entry) => HashCode.Combine(hash, entry.Key, entry.Value)),
            vector => new Dictionary<Guid, long>(vector));

    private static readonly JsonSerializerOptions VersionVectorJson = new(JsonSerializerDefaults.Web);

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

            // §5's watermark. Read on the ingest path to decide resync_required
            // and written only by the frontier job, so it lives beside the
            // document rather than in a table needing a join per submission.
            entity.Property(e => e.StabilityFrontier)
                .HasColumnName("stability_frontier")
                .HasColumnType("jsonb")
                .HasConversion(VersionVectorConverter, VersionVectorComparer);
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

            // §9's "list what I can reach" reads this table by user. Without
            // the index that is a sequential scan of every membership in the
            // system on a page the client loads first.
            entity.HasIndex(e => e.UserId);
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

            // jsonb rather than a side table. It is read once per frontier
            // computation and written once per acknowledgement, always whole
            // and always by primary key — there is no query that wants its
            // entries individually, and a table would add a join to every
            // frontier computation to buy nothing.
            entity.Property(e => e.Acknowledged)
                .HasColumnName("acknowledged")
                .HasColumnType("jsonb")
                .HasConversion(VersionVectorConverter, VersionVectorComparer);
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

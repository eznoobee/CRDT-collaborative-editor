using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Editor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_members",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    granted_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_members", x => new { x.document_id, x.user_id });
                });

            // document_ops is created by hand because EF cannot express hash
            // partitioning, and §6 requires it. Postgres also requires the
            // partition key to be part of every unique constraint, which is why
            // document_id leads the primary key.
            migrationBuilder.Sql("""
                CREATE TABLE document_ops (
                    document_id           uuid        NOT NULL,
                    replica_id            uuid        NOT NULL,
                    seq                   bigint      NOT NULL,
                    op_type               varchar(16) NOT NULL,
                    parent_replica        uuid        NULL,
                    parent_seq            bigint      NULL,
                    side                  varchar(1)  NULL,
                    right_origin_replica  uuid        NULL,
                    right_origin_seq      bigint      NULL,
                    right_origin_is_end   boolean     NOT NULL DEFAULT false,
                    value                 varchar(8)  NULL,
                    target_replica        uuid        NULL,
                    target_seq            bigint      NULL,
                    server_seq            bigint      NOT NULL,
                    created_at            timestamptz NOT NULL,

                    CONSTRAINT "PK_document_ops"
                        PRIMARY KEY (document_id, replica_id, seq),

                    -- Sequence numbers are dense from zero (§5), and Postgres
                    -- has no unsigned bigint, so negatives are a bug not a value.
                    CONSTRAINT ck_document_ops_seq_non_negative
                        CHECK (seq >= 0 AND server_seq >= 0),

                    CONSTRAINT ck_document_ops_op_type
                        CHECK (op_type IN ('insert', 'delete')),

                    -- An insert carries a value and a side; a delete carries a
                    -- target and neither. Enforcing the shape here means a
                    -- half-formed operation cannot reach the log at all.
                    CONSTRAINT ck_document_ops_insert_shape CHECK (
                        op_type <> 'insert' OR (
                            value IS NOT NULL
                            AND side IN ('L', 'R')
                            AND target_replica IS NULL
                            AND target_seq IS NULL
                        )
                    ),
                    CONSTRAINT ck_document_ops_delete_shape CHECK (
                        op_type <> 'delete' OR (
                            value IS NULL
                            AND side IS NULL
                            AND parent_replica IS NULL
                            AND parent_seq IS NULL
                            AND right_origin_replica IS NULL
                            AND right_origin_seq IS NULL
                            AND right_origin_is_end = false
                            AND target_replica IS NOT NULL
                            AND target_seq IS NOT NULL
                        )
                    ),

                    -- Only right children carry a right origin, and "end of
                    -- document" is a right origin rather than the absence of one
                    -- (§5). A left child asserting either would not round-trip.
                    CONSTRAINT ck_document_ops_right_origin CHECK (
                        side IS DISTINCT FROM 'L'
                        OR (right_origin_replica IS NULL
                            AND right_origin_seq IS NULL
                            AND right_origin_is_end = false)
                    ),
                    CONSTRAINT ck_document_ops_right_origin_exclusive CHECK (
                        NOT (right_origin_is_end AND right_origin_replica IS NOT NULL)
                    ),

                    -- An id is both halves or neither.
                    CONSTRAINT ck_document_ops_parent_pair
                        CHECK ((parent_replica IS NULL) = (parent_seq IS NULL)),
                    CONSTRAINT ck_document_ops_right_origin_pair
                        CHECK ((right_origin_replica IS NULL) = (right_origin_seq IS NULL)),
                    CONSTRAINT ck_document_ops_target_pair
                        CHECK ((target_replica IS NULL) = (target_seq IS NULL))
                ) PARTITION BY HASH (document_id);
                """);

            // Sixteen partitions: enough to spread contention across documents,
            // few enough that a catch-up scan does not fan out absurdly. Changing
            // the count later means rewriting the table, so it is a schema
            // decision rather than a tuning knob.
            for (var partition = 0; partition < 16; partition++)
            {
                migrationBuilder.Sql(
                    $"""
                     CREATE TABLE document_ops_p{partition}
                         PARTITION OF document_ops
                         FOR VALUES WITH (MODULUS 16, REMAINDER {partition});
                     """);
            }

            migrationBuilder.CreateTable(
                name: "document_replicas",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replica_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operation_count = table.Column<long>(type: "bigint", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_replicas", x => new { x.document_id, x.replica_id });
                });

            migrationBuilder.CreateTable(
                name: "document_snapshots",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_seq = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    version_vector = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_snapshots", x => new { x.document_id, x.server_seq });
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    oidc_issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    oidc_subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_ops_document_id_server_seq",
                table: "document_ops",
                columns: new[] { "document_id", "server_seq" });

            migrationBuilder.CreateIndex(
                name: "IX_users_oidc_issuer_oidc_subject",
                table: "users",
                columns: new[] { "oidc_issuer", "oidc_subject" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_members");

            migrationBuilder.DropTable(
                name: "document_ops");

            migrationBuilder.DropTable(
                name: "document_replicas");

            migrationBuilder.DropTable(
                name: "document_snapshots");

            migrationBuilder.DropTable(
                name: "documents");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}

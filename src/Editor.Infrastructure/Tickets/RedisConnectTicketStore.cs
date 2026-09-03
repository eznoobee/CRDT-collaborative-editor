using System.Buffers.Text;
using System.Security.Cryptography;
using Editor.Domain;
using StackExchange.Redis;

// StackExchange.Redis has a Role type of its own, for replication topology.
using Role = Editor.Domain.Role;

namespace Editor.Infrastructure.Tickets;

/// <summary>
/// Connect tickets in Redis, redeemed with <c>GETDEL</c> (PROJECT_SPEC.md §7).
/// </summary>
/// <remarks>
/// Redis rather than process memory because §8 forbids sticky sessions: the
/// instance that issues a ticket is usually not the one that redeems it.
/// <para>
/// <c>GETDEL</c> rather than a read followed by a delete because single-use has
/// to be one atomic operation. Under read-then-delete two connects arriving
/// together both observe the ticket present and both proceed, which is exactly
/// the replay single-use exists to stop — and it passes every test written
/// against one client.
/// </para>
/// </remarks>
public sealed class RedisConnectTicketStore : IConnectTicketStore
{
    /// <summary>
    /// Ticket entropy. 256 bits, because the ticket is the only thing standing
    /// between an attacker who can read a proxy log line and a live connection,
    /// and because guessing must be hopeless rather than merely expensive.
    /// </summary>
    private const int TicketBytes = 32;

    /// <summary>
    /// 16 + 16 + 16 bytes of id, one byte of role, then the 16-byte claim token.
    /// </summary>
    /// <remarks>
    /// The claim token rides in the ticket because §7 takes the replica claim at
    /// <c>negotiate</c> and releases it on disconnect, and those happen on
    /// different instances. Anything else would mean the hub releasing a claim
    /// by key alone — which lets a stalled session drop the live claim of the
    /// session that replaced it.
    /// </remarks>
    private const int PayloadBytes = 65;

    private readonly IConnectionMultiplexer _redis;
    private readonly ConnectTicketOptions _options;

    public RedisConnectTicketStore(IConnectionMultiplexer redis, ConnectTicketOptions options)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);

        // Checked here as well as by the options validation, because this is
        // the guard that cannot be routed around: any caller constructing the
        // store directly still gets §7's ceiling.
        if (options.Lifetime <= TimeSpan.Zero || options.Lifetime > ConnectTicketOptions.MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Lifetime,
                $"§7 caps connect ticket lifetime at {ConnectTicketOptions.MaximumLifetime}.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.KeyPrefix);

        _redis = redis;
        _options = options;
    }

    public async Task<string> IssueAsync(ConnectionBinding binding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ticket = NewTicket();
        var stored = await _redis.GetDatabase().StringSetAsync(
            Key(ticket),
            Encode(binding),
            _options.Lifetime,
            // A ticket that collided with a live one would silently replace it,
            // logging the first holder out and handing the second a binding
            // that is not theirs. At 256 bits this never happens; When.NotExists
            // makes "never" a failure rather than an assumption.
            When.NotExists).ConfigureAwait(false);

        if (!stored)
        {
            throw new InvalidOperationException("Connect ticket collided with a live ticket.");
        }

        return ticket;
    }

    /// <remarks>
    /// There is no clock here on purpose. Expiry is the Redis key TTL set at
    /// issue, so a ticket stops being redeemable without anything having to run
    /// — no sweeper, and no instance whose clock skew decides whether an
    /// expired ticket still works.
    /// </remarks>
    public async Task<ConnectionBinding?> RedeemAsync(string ticket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(ticket) || !IsWellFormed(ticket))
        {
            // Rejected before it reaches Redis. A ticket is 43 base64url
            // characters; anything else — a JWT, a key-glob, a path — is not a
            // ticket, and interpolating it into a key would be the injection
            // this check exists to prevent.
            return null;
        }

        // GETDEL. The single Redis round trip that makes single-use true.
        var payload = await _redis.GetDatabase()
            .StringGetDeleteAsync(Key(ticket))
            .ConfigureAwait(false);

        if (payload.IsNullOrEmpty)
        {
            return null;
        }

        return Decode((byte[])payload!);
    }

    public async Task<bool> ExistsAsync(string ticket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(ticket) || !IsWellFormed(ticket))
        {
            return false;
        }

        return await _redis.GetDatabase().KeyExistsAsync(Key(ticket)).ConfigureAwait(false);
    }

    private string Key(string ticket) => _options.KeyPrefix + ticket;

    private static string NewTicket() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TicketBytes));

    /// <summary>
    /// Whether a value has the shape of a ticket this store issued.
    /// </summary>
    private static bool IsWellFormed(string ticket)
    {
        // 32 bytes of base64url without padding is exactly 43 characters.
        if (ticket.Length != 43)
        {
            return false;
        }

        foreach (var c in ticket)
        {
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Encode(ConnectionBinding binding)
    {
        var payload = new byte[PayloadBytes];
        var span = payload.AsSpan();

        Write(span[..16], binding.UserId);
        Write(span.Slice(16, 16), binding.DocumentId);
        Write(span.Slice(32, 16), binding.ReplicaId);

        // Roles are a closed set; an unrecognised one is a programming error
        // here rather than something to store and puzzle over on redemption.
        if (!Enum.IsDefined(binding.Role))
        {
            throw new ArgumentOutOfRangeException(nameof(binding), binding.Role, "Unknown role.");
        }

        payload[48] = checked((byte)binding.Role);
        Write(span.Slice(49, 16), binding.ClaimToken);
        return payload;

        static void Write(Span<byte> destination, Guid value)
        {
            // Big-endian, so a stored ticket reads the same on any host. The
            // ticket outlives the process that wrote it and may be redeemed by
            // a different one.
            var written = value.TryWriteBytes(destination, bigEndian: true, out var count);
            if (!written || count != 16)
            {
                throw new InvalidOperationException("Failed to write a Guid.");
            }
        }
    }

    private static ConnectionBinding? Decode(byte[] payload)
    {
        if (payload.Length != PayloadBytes)
        {
            // Someone else's key in our keyspace, or a value from a format we
            // no longer speak. Either way this is not a binding, and guessing
            // at one would hand a connection whatever the bytes happened to
            // decode to.
            return null;
        }

        var role = (Role)payload[48];
        if (!Enum.IsDefined(role))
        {
            return null;
        }

        return new ConnectionBinding(
            new Guid(payload.AsSpan(0, 16), bigEndian: true),
            new Guid(payload.AsSpan(16, 16), bigEndian: true),
            new Guid(payload.AsSpan(32, 16), bigEndian: true),
            role,
            new Guid(payload.AsSpan(49, 16), bigEndian: true));
    }
}

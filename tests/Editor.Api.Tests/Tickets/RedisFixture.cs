using StackExchange.Redis;
using Testcontainers.Redis;

namespace Editor.Api.Tests.Tickets;

/// <summary>
/// A real Redis for the connect-ticket tests (PROJECT_SPEC.md §7).
/// </summary>
/// <remarks>
/// Real rather than faked, because the property under test is that redemption
/// is one atomic server-side operation. A fake would be a second implementation
/// of the semantics being asserted, and it would pass whether or not Redis
/// behaves that way.
/// </remarks>
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;
    private ConnectionMultiplexer? _multiplexer;

    /// <summary>The configuration string the fixture's Redis answers on.</summary>
    public string Configuration { get; private set; } = string.Empty;

    public IConnectionMultiplexer Redis =>
        _multiplexer ?? throw new InvalidOperationException(SkipReason ?? "Redis is not available.");

    /// <summary>Why there is no Redis, when there is none.</summary>
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("EDITOR_TEST_REDIS");
        string configuration;

        if (!string.IsNullOrWhiteSpace(external))
        {
            configuration = external;
        }
        else
        {
            try
            {
                _container = new RedisBuilder("redis:7-alpine").Build();
                await _container.StartAsync(TestContext.Current.CancellationToken);
                configuration = _container.GetConnectionString();
            }
            catch (Exception ex)
            {
                // Same posture as the persistence fixture: a missing daemon is
                // a skip on a developer machine and a failure in CI, where
                // Phase 3's §7 tickets must actually be exercised.
                if (string.Equals(
                        Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Redis is required for the connect-ticket tests in CI "
                        + "(PROJECT_SPEC.md §7). Set EDITOR_TEST_REDIS or provide Docker.",
                        ex);
                }

                SkipReason = $"No Redis: Docker is unavailable and EDITOR_TEST_REDIS is unset ({ex.Message})";
                return;
            }
        }

        Configuration = configuration;
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
    }

    /// <summary>Skips the calling test when no Redis could be reached.</summary>
    public void RequireRedis() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);

    public async ValueTask DisposeAsync()
    {
        if (_multiplexer is not null)
        {
            await _multiplexer.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>Shares one Redis across the connect-ticket tests.</summary>
[CollectionDefinition(nameof(RedisTests))]
public sealed class RedisTests : ICollectionFixture<RedisFixture>;

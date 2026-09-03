using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Editor.Api.Tests.Logging;

/// <summary>
/// A log sink that keeps everything, so a test can assert what reached it.
/// </summary>
/// <remarks>
/// It records three renderings of every record, because a real provider might
/// use any of them: the formatted message, the structured state as key-value
/// pairs, and the exception. A test that checked only the message would pass
/// against a JSON provider that writes the credential into a field.
/// </remarks>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<string> _records = new();
    private IExternalScopeProvider? _scopes;

    /// <summary>Everything written, in every rendering.</summary>
    public IReadOnlyCollection<string> Records => _records.ToArray();

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose() => GC.SuppressFinalize(this);

    private void Record(string text) => _records.Enqueue(text);

    private sealed class Sink : ILogger
    {
        private readonly CapturingLoggerProvider _owner;
        private readonly string _category;

        public Sink(CapturingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            _owner._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            _owner.Record($"{_category}|message|{formatter(state, exception)}");

            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    _owner.Record(string.Create(
                        CultureInfo.InvariantCulture, $"{_category}|state|{pair.Key}={pair.Value}"));
                }
            }
            else
            {
                _owner.Record($"{_category}|state|{state}");
            }

            if (exception is not null)
            {
                _owner.Record($"{_category}|exception|{exception}");
            }

            // Scopes are read as key-value pairs and as text, because a
            // structured provider enumerates them and a console provider
            // renders them. Recording only the rendering hides a credential
            // sitting in a scope value whose container has an unhelpful
            // ToString — which is exactly what a Dictionary scope has.
            _owner._scopes?.ForEachScope(
                static (scope, sink) =>
                {
                    sink._owner.Record($"{sink._category}|scope|{scope}");

                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        foreach (var pair in pairs)
                        {
                            sink._owner.Record(string.Create(
                                CultureInfo.InvariantCulture, $"{sink._category}|scope|{pair.Key}={pair.Value}"));
                        }
                    }
                },
                this);
        }
    }
}

using System.Collections;

namespace Editor.Api.Logging;

/// <summary>
/// Wraps the real logger factory and redacts credentials before any provider
/// sees them (PROJECT_SPEC.md §7).
/// </summary>
/// <remarks>
/// The seam is the factory rather than a middleware or a single provider,
/// because §7 says "no sink" and because the leak this guards against comes
/// from code the application does not own. ASP.NET Core's hosting layer logs
/// the request URL, query string included, before the first middleware runs;
/// an exception's own message may quote that URL; and a provider added later
/// would otherwise be a new sink with no redaction in front of it.
/// </remarks>
public sealed class RedactingLoggerFactory : ILoggerFactory
{
    private readonly ILoggerFactory _inner;

    public RedactingLoggerFactory(ILoggerFactory inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public ILogger CreateLogger(string categoryName) =>
        new RedactingLogger(_inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

    public void Dispose() => _inner.Dispose();

    private sealed class RedactingLogger : ILogger
    {
        private readonly ILogger _inner;

        public RedactingLogger(ILogger inner) => _inner = inner;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            _inner.BeginScope(Redact(state) ?? state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var message = SecretRedaction.Apply(formatter(state, exception));

            // The state is passed on as well as the message: a structured
            // provider reads the key-value pairs and never calls the formatter,
            // so redacting only the message would leave the credential in the
            // JSON.
            var redactedState = Redact(state) ?? state;
            var redactedException = RedactException(exception);

            _inner.Log(logLevel, eventId, redactedState, redactedException, (_, _) => message);
        }

        /// <summary>
        /// A redacted copy of <paramref name="state"/>, or <see langword="null"/>
        /// when nothing needed changing.
        /// </summary>
        private static object? Redact<TState>(TState state)
        {
            // IEnumerable, not IReadOnlyList: a scope is very often a
            // Dictionary, which is neither a list nor a thing whose ToString
            // reveals its contents. Matching only the list shape leaves a
            // credential in a scope value untouched, and invisible to any test
            // whose sink renders scopes rather than enumerating them.
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                return RedactedState.From(pairs, state?.ToString());
            }

            var text = state?.ToString();
            return SecretRedaction.Contains(text) ? SecretRedaction.Apply(text) : null;
        }

        private static Exception? RedactException(Exception? exception)
        {
            if (exception is null)
            {
                return null;
            }

            var rendered = exception.ToString();
            return SecretRedaction.Contains(rendered)
                ? new RedactedException(SecretRedaction.Apply(rendered))
                : exception;
        }
    }

    /// <summary>
    /// Log state with every string value redacted, still readable as the
    /// key-value list structured providers expect.
    /// </summary>
    private sealed class RedactedState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly List<KeyValuePair<string, object?>> _pairs;
        private readonly string _text;

        private RedactedState(List<KeyValuePair<string, object?>> pairs, string text)
        {
            _pairs = pairs;
            _text = text;
        }

        public static object From(
            IEnumerable<KeyValuePair<string, object?>> pairs, string? original)
        {
            var redacted = new List<KeyValuePair<string, object?>>();
            var changed = false;

            foreach (var pair in pairs)
            {
                // Values are redacted by rendering them, which is what a
                // provider would do anyway. A non-string value carrying a
                // credential — a Uri, a header collection — renders to text
                // containing it, and that is what would reach the sink.
                var rendered = pair.Value?.ToString();

                if (SecretRedaction.IsSensitiveName(pair.Key))
                {
                    // The whole value goes, not the parts of it a pattern
                    // recognises: a field called Ticket holds a ticket.
                    redacted.Add(new KeyValuePair<string, object?>(pair.Key, SecretRedaction.Placeholder));
                    changed = true;
                }
                else if (SecretRedaction.Contains(rendered))
                {
                    redacted.Add(new KeyValuePair<string, object?>(pair.Key, SecretRedaction.Apply(rendered)));
                    changed = true;
                }
                else
                {
                    redacted.Add(pair);
                }
            }

            var text = SecretRedaction.Apply(original);
            changed |= !string.Equals(text, original, StringComparison.Ordinal);

            return changed ? new RedactedState(redacted, text) : pairs;
        }

        public KeyValuePair<string, object?> this[int index] => _pairs[index];

        public int Count => _pairs.Count;

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _pairs.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => _text;
    }
}

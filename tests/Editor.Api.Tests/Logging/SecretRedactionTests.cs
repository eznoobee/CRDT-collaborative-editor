using Editor.Api.Logging;

namespace Editor.Api.Tests.Logging;

/// <summary>Each pattern §7's redaction depends on, on its own.</summary>
/// <remarks>
/// The sentinel test proves the pipeline is wired up. These prove the patterns
/// cover what they claim to, including the bare-ticket rule the sentinel
/// deliberately avoids triggering.
/// </remarks>
public sealed class SecretRedactionTests
{
    [Theory]
    // The hosting layer's request log, which is where the ticket actually lands.
    [InlineData(
        "Request starting HTTP/1.1 GET http://host/hub?access_token=abc123&doc=42 - - -",
        "Request starting HTTP/1.1 GET http://host/hub?access_token=[redacted]&doc=42 - - -")]
    [InlineData("?access_token=abc", "?access_token=[redacted]")]
    [InlineData("&ticket=abc&x=1", "&ticket=[redacted]&x=1")]
    [InlineData("ACCESS_TOKEN=abc", "ACCESS_TOKEN=[redacted]")]
    // Header renderings.
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: [redacted]")]
    [InlineData("Bearer abc.def.ghi", "Bearer [redacted]")]
    [InlineData("Authorization=Bearer abc", "Authorization=[redacted]")]
    // A bare ticket: 43 base64url characters, exactly what the store issues.
    [InlineData(
        "connect refused for 8Zk3qP0vLmX1nR7tY2wB4hJ6sD9fG5cA0eU8iO3pQ1k",
        "connect refused for [redacted]")]
    public void A_credential_is_removed(string input, string expected) =>
        Assert.Equal(expected, SecretRedaction.Apply(input));

    [Theory]
    // Nothing that is not a credential should be touched: a redactor that ate
    // ordinary text would be turned off by the first person who needed a log.
    [InlineData("Request finished HTTP/1.1 GET http://host/health/live - 200")]
    [InlineData("doc=42&replica=7")]
    [InlineData("user 4d0f1e0a-2c3b-4a5d-8e9f-0a1b2c3d4e5f joined")]
    [InlineData("applied 128 operations in 3.4ms")]
    [InlineData("")]
    public void Ordinary_text_is_left_alone(string input)
    {
        Assert.Equal(input, SecretRedaction.Apply(input));
        Assert.False(SecretRedaction.Contains(input));
    }

    [Fact]
    public void Redaction_is_idempotent()
    {
        // The pipeline can pass a message through more than once — a wrapped
        // logger, a re-log of a caught exception — and a second pass that
        // redacted "[redacted]" again would corrupt the line.
        const string Input = "GET /hub?access_token=abc with Authorization: Bearer xyz";

        var once = SecretRedaction.Apply(Input);

        Assert.Equal(once, SecretRedaction.Apply(once));
    }

    [Theory]
    [InlineData("Ticket")]
    [InlineData("ticket")]
    [InlineData("access_token")]
    [InlineData("Authorization")]
    [InlineData("TOKEN")]
    public void A_field_named_for_a_credential_is_sensitive(string name) =>
        Assert.True(SecretRedaction.IsSensitiveName(name));

    [Theory]
    [InlineData("DocumentId")]
    [InlineData("ReplicaId")]
    [InlineData("Path")]
    [InlineData("StatusCode")]
    [InlineData(null)]
    public void An_ordinary_field_name_is_not(string? name) =>
        Assert.False(SecretRedaction.IsSensitiveName(name));

    [Fact]
    public void A_null_input_redacts_to_empty_rather_than_throwing()
    {
        // Log state is full of nulls, and a redactor that threw would take down
        // the request it was trying to describe.
        Assert.Equal(string.Empty, SecretRedaction.Apply(null));
        Assert.False(SecretRedaction.Contains(null));
    }
}

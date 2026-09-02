using Microsoft.AspNetCore.Mvc.Testing;

namespace Editor.Api.Tests.Authentication;

/// <summary>
/// The §7 property that is about the whole host rather than one options object.
/// </summary>
public sealed class HostStartupTests
{
    [Fact]
    public void The_host_refuses_to_start_with_no_issuer_configured()
    {
        // AddEditorAuthentication has its own test for this. That one proves the
        // extension method throws; this one proves the app calls it, and calls it
        // somewhere that runs before the first request. Wiring the method up and
        // then never invoking it would leave the unit test green and every
        // deployment unauthenticated.
        //
        // appsettings.json carries no Oidc section, so this is the real default
        // configuration of a deployment that forgot to set one.
        using var factory = new WebApplicationFactory<Program>();

        var thrown = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(thrown);
        Assert.Contains(
            "Oidc",
            Flatten(thrown),
            StringComparison.Ordinal);
    }

    private static string Flatten(Exception exception)
    {
        var text = exception.Message;
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            text += "\n" + inner.Message;
        }

        return text;
    }
}

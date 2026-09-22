namespace Editor.Api.Tests.Load;

/// <summary>
/// Where a measurement's numbers go, besides the test output.
/// </summary>
/// <remarks>
/// <para>
/// A runner shows the output of failing tests and hides the output of passing
/// ones, which for a measurement is backwards: the numbers are the deliverable
/// whether or not they cleared a threshold, and the ones that passed are the
/// ones nobody would otherwise see. §8 requires them recorded with their
/// provenance, so they are written where they can be read after the run.
/// </para><para>
/// Appended rather than overwritten within a run, and truncated once per
/// process, so a file holds one run and not a mixture of two — a report
/// containing numbers from two different builds is the failure §8's provenance
/// rule exists to prevent, arriving through the back door.
/// </para>
/// </remarks>
public static class Results
{
    private static readonly Lock Gate = new();
    private static bool _started;

    /// <summary>The file the run writes to, if one was asked for.</summary>
    public static string? Path => Environment.GetEnvironmentVariable("EDITOR_LOAD_REPORT");

    public static void Write(string line)
    {
        var path = Path;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (Gate)
        {
            if (!_started)
            {
                var directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, string.Empty);
                _started = true;
            }

            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}

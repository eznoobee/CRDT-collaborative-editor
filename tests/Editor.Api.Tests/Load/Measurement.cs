using System.Globalization;
using System.Runtime.InteropServices;

namespace Editor.Api.Tests.Load;

/// <summary>
/// What §8 requires alongside every performance number.
/// </summary>
/// <remarks>
/// <para>
/// §8: "Every performance number is reported with the build that produced it,
/// and a number without one is not a result." A figure missing the
/// configuration, the runner class and the commit cannot be compared to a later
/// figure, which makes it unfalsifiable — it can never be shown to have
/// regressed, only replaced.
/// </para><para>
/// This is a record rather than a comment because §8's rule was written from a
/// near-miss: the end-to-end harness was found to be building a development
/// bundle, and answering "which build was that?" took reading the harness
/// rather than reading the number.
/// </para>
/// </remarks>
public sealed record Provenance(
    string Configuration,
    string Commit,
    bool Clean,
    string Runtime,
    string Os,
    string Cpu,
    int Cores,
    double MemoryGiB)
{
    public static Provenance Current()
    {
        return new Provenance(
            Configuration:
#if DEBUG
                "Debug",
#else
                "Release",
#endif
            Commit: Git("rev-parse --short HEAD"),
            Clean: Git("status --porcelain").Length == 0,
            Runtime: RuntimeInformation.FrameworkDescription,
            Os: RuntimeInformation.OSDescription,
            Cpu: CpuModel(),
            Cores: Environment.ProcessorCount,
            MemoryGiB: Math.Round(
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024), 1));
    }

    /// <summary>
    /// One line, for the top of every reported result.
    /// </summary>
    /// <remarks>
    /// The dirty marker matters more than it looks: a number measured against
    /// uncommitted work names a commit that does not contain what was measured,
    /// which is worse than naming none at all.
    /// </remarks>
    public override string ToString() =>
        $"{Configuration} · {Commit}{(Clean ? "" : "+dirty")} · {Runtime} · {Os} · "
        + $"{Cpu} × {Cores} · {MemoryGiB.ToString(CultureInfo.InvariantCulture)} GiB";

    private static string CpuModel()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.Ordinal))
                {
                    return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                }
            }
        }
        catch (IOException)
        {
            // Not Linux, or /proc is not mounted. The field says so rather than
            // reporting a model nobody measured on.
        }

        return "unknown";
    }

    private static string Git(string arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });

            if (process is null)
            {
                return "unknown";
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5_000);
            return output.Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }
}

/// <summary>
/// A percentile that carries its sample count, because §8 requires it.
/// </summary>
/// <remarks>
/// §8: "A p99 over 50 requests is the worst of 50. The sample count is part of
/// the reported result; a percentile without one is decoration." So the count is
/// a field rather than something the caller may forget to print, and
/// <see cref="ToString"/> always prints it.
/// </remarks>
public readonly record struct Percentiles(int Count, double Min, double P50, double P95, double P99, double Max)
{
    /// <summary>
    /// The smallest sample size at which a p99 is one of the worst few rather
    /// than the single worst observation.
    /// </summary>
    /// <remarks>
    /// At n = 100 the p99 <em>is</em> the maximum, so it says nothing the
    /// maximum does not. A thousand puts ten samples above the p99, which is
    /// the point at which the number starts describing a tail rather than an
    /// outlier.
    /// </remarks>
    public const int MinimumSamples = 1_000;

    public static Percentiles Of(IReadOnlyCollection<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return new Percentiles(0, 0, 0, 0, 0, 0);
        }

        var sorted = samples.ToArray();
        Array.Sort(sorted);

        return new Percentiles(
            sorted.Length,
            sorted[0],
            At(sorted, 0.50),
            At(sorted, 0.95),
            At(sorted, 0.99),
            sorted[^1]);
    }

    /// <summary>
    /// Nearest-rank, which reports an observation that actually happened.
    /// </summary>
    /// <remarks>
    /// Interpolating between two samples produces a number no request took, and
    /// at the tail — where the samples are sparsest and the distances between
    /// them largest — that is exactly where the invention is biggest.
    /// </remarks>
    private static double At(double[] sorted, double quantile)
    {
        var rank = (int)Math.Ceiling(quantile * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"n={Count} min={Min:F2} p50={P50:F2} p95={P95:F2} p99={P99:F2} max={Max:F2}");
}

/// <summary>
/// How hard the box was working while the number was taken.
/// </summary>
/// <remarks>
/// §8's second rule: "If the load harness saturates first, the number describes
/// the harness. The generator's own utilisation is reported beside every result,
/// or the result is not one." For an in-process harness the generator and the
/// server share a process, so what is reported is the whole process against the
/// whole box — which is the conservative reading: everything the generator
/// spends is unavailable to the server.
/// </remarks>
public readonly record struct Utilisation(double CpuSeconds, double WallSeconds, int Cores)
{
    /// <summary>Fraction of the box's total CPU capacity consumed.</summary>
    public double Fraction => WallSeconds <= 0 ? 0 : CpuSeconds / (WallSeconds * Cores);

    /// <summary>
    /// Above this, the box is the constraint and the number describes it.
    /// </summary>
    /// <remarks>
    /// Not 1.0. A run that averages 85% of every core has been at 100% for part
    /// of it, and the p99 is drawn from exactly those moments.
    /// </remarks>
    public const double Saturated = 0.85;

    public bool IsSaturated => Fraction >= Saturated;

    public static Utilisation Since(TimeSpan cpuAtStart, System.Diagnostics.Stopwatch wall) =>
        new(
            (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpuAtStart).TotalSeconds,
            wall.Elapsed.TotalSeconds,
            Environment.ProcessorCount);

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Fraction:P0} of {Cores} cores ({CpuSeconds:F1}s CPU over {WallSeconds:F1}s)"
            + $"{(IsSaturated ? " — SATURATED, this number describes the box" : "")}");
}

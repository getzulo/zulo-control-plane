using System.Reflection;

namespace ZuloOne.ControlPlane;

/// <summary>
/// What this binary is, for the times someone has to say which one they were
/// running.
/// </summary>
/// <remarks>
/// The same helper exists in ZuloOne.Core, deliberately duplicated rather than
/// shared: the two repositories build and release independently, and a shared
/// package for eight lines would couple their release cycles for nothing.
///
/// <para>
/// A version number alone is not enough. AssemblyVersion carries only
/// Major.Minor.Build, so two images cut from different commits on the same day
/// report the same string — and for the panel that string is <c>2026.0.N</c>,
/// where N is a CI run number that says nothing about what changed. The commit
/// does.
/// </para>
/// </remarks>
public static class AppBuild
{
    /// <summary>
    /// The source revision the assembly was built from, or an empty string when it
    /// was built without one (a local <c>dotnet build</c>, say).
    ///
    /// MSBuild appends <c>+&lt;SourceRevisionId&gt;</c> to InformationalVersion, so
    /// this reads back what the build passed in — no separate attribute, and
    /// nothing to keep in step by hand.
    /// </summary>
    public static string Revision { get; } = ReadRevision();

    /// <summary>Version as the assembly carries it, Major.Minor.Build.</summary>
    public static string Version { get; } =
        typeof(AppBuild).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// When this process started. A panel that has been up for three minutes after
    /// an upgrade and one that has been up for three weeks answer questions
    /// differently, and the difference is invisible without this.
    /// </summary>
    public static DateTime StartedUtc { get; } = DateTime.UtcNow;

    private static string ReadRevision()
    {
        var informational = typeof(AppBuild).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational)) return string.Empty;

        var plus = informational.IndexOf('+');
        return plus >= 0 && plus < informational.Length - 1
            ? informational[(plus + 1)..]
            : string.Empty;
    }
}

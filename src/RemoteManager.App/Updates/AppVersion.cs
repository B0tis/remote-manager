using System.Reflection;

namespace RemoteManager.App.Updates;

/// <summary>
/// The version the release script stamped into the assembly (<c>-p:Version=…</c>): "0.4.1", plus the commit
/// the SDK appends to the informational version. A plain build says "0.0.0-dev".
/// </summary>
public static class AppVersion
{
    private static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string Current { get; } = Informational.Split('+')[0];

    /// <summary>Short commit id, or null when the build had none.</summary>
    public static string? Commit { get; } =
        Informational.Split('+') is [_, { Length: > 0 } sha, ..] ? sha[..Math.Min(7, sha.Length)] : null;
}

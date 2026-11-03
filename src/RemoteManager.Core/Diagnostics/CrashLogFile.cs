using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteManager.Core.Diagnostics;

/// <summary>
/// Writes one crash report per error into a folder (the app uses <see cref="AppPaths.Logs"/>): time, where it
/// happened, version, OS, and the exception with its stack. Nothing else: no settings, no connection data, no
/// secrets. Never throws; a crash report must not cause the next crash.
/// </summary>
public static class CrashLogFile
{
    /// <summary>How many reports are kept; older ones are removed when a new one is written.</summary>
    public const int Keep = 20;

    /// <summary>Writes the report and returns its path, or null when it could not be written.</summary>
    public static string? Write(string folder, Exception exception, string source, string version, DateTimeOffset? now = null)
    {
        try
        {
            DateTimeOffset time = now ?? DateTimeOffset.Now;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"crash-{time:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
            File.WriteAllText(path, Format(exception, source, version, time), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Prune(folder);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static string Format(Exception exception, string source, string version, DateTimeOffset time)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Remote-Manager crash report");
        text.AppendLine(CultureInfo.InvariantCulture, $"Time:      {time:O}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Source:    {source}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Version:   {version}");
        text.AppendLine(CultureInfo.InvariantCulture, $"OS:        {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Runtime:   {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        text.AppendLine();
        // Type, message, and stack of the exception and every inner one (ToString); Exception.Data is left out.
        text.AppendLine(exception.ToString());
        return text.ToString();
    }

    private static void Prune(string folder)
    {
        foreach (FileInfo old in new DirectoryInfo(folder).GetFiles("crash-*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Keep))
        {
            try
            {
                old.Delete();
            }
            catch (IOException)
            {
            }
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Stand-in for TeamViewer/AnyDesk/RustDesk in the self-test: the self-test points the tool path at this
/// exe and sets REMOTE_MANAGER_FAKE_TOOL to a report file. The process reads one line from stdin (the
/// AnyDesk password), writes only its SHA-256 and the argument count to the report (never a value),
/// and stays alive so the self-test can read its command line from Win32_Process.
/// </summary>
internal static class FakeRemoteTool
{
    public const string Variable = "REMOTE_MANAGER_FAKE_TOOL";

    public static int Run(string reportPath, string[] args)
    {
        string stdinHash = "none";
        if (Console.IsInputRedirected)
        {
            string line = Console.In.ReadLine() ?? "";
            stdinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
        }

        File.WriteAllLines(reportPath, [$"args={args.Length}", $"stdin={stdinHash}"]);
        Thread.Sleep(TimeSpan.FromSeconds(30));
        return 0;
    }
}

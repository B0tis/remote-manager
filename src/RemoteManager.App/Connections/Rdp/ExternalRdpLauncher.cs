using System.ComponentModel;
using System.Diagnostics;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Rdp;

namespace RemoteManager.App.Connections.Rdp;

/// <summary>A prepared mstsc start: the .rdp file, the switches, and what to clean up afterwards.</summary>
internal sealed record ExternalRdpStart(string RdpPath, IReadOnlyList<string> Arguments, string Host, bool WroteCredential)
{
    /// <summary>Deletes the .rdp file and the TERMSRV entry this start created (never one the user had).</summary>
    public void CleanUp()
    {
        if (WroteCredential)
            TermsrvCredential.Delete(Host);
        try
        {
            File.Delete(RdpPath);
            Directory.Delete(Path.GetDirectoryName(RdpPath)!);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Starts an RDP connection with mstsc: writes an .rdp file with the start parameters (no password),
/// puts the stored password as TERMSRV/&lt;host&gt; into the Credential Manager for the logon session,
/// and removes both when mstsc exits. Used for "Mit mstsc öffnen" and for RemoteApps.
/// </summary>
internal static class ExternalRdpLauncher
{
    public static string MstscPath => Path.Combine(Environment.SystemDirectory, "mstsc.exe");

    /// <summary>Writes the file and the credential; nothing is started yet.</summary>
    public static async Task<ExternalRdpStart> PrepareAsync(Connection connection, CredentialService credentials)
    {
        EffectiveCredential logon = credentials.Resolve(connection);
        string host = connection.Host.Trim();

        string folder = Path.Combine(Path.GetTempPath(), "RemoteManager", "rdp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{SafeFileName(connection.Name)}.rdp");
        RdpFileMapping.ToRdpFile(connection, logon.UserName, logon.Domain).Save(path);

        // An entry the user saved in mstsc stays untouched (and is used by mstsc).
        bool wrote = false;
        if (logon.HasSecret && logon.UserName.Length > 0 && !TermsrvCredential.Exists(host))
        {
            string? password = await credentials.ReadAsync(logon.Secret);
            if (password is { Length: > 0 })
            {
                TermsrvCredential.Write(host, RdpFileMapping.FormatUser(logon.UserName, logon.Domain), password);
                wrote = true;
            }
        }

        return new ExternalRdpStart(path, [path, .. RdpFileMapping.MstscSwitches(connection.SettingsAs<RdpSettings>())], host, wrote);
    }

    /// <summary>Starts mstsc; cleans up when it exits (or right away when it cannot start).</summary>
    public static Process Start(ExternalRdpStart start)
    {
        var info = new ProcessStartInfo(MstscPath) { UseShellExecute = false };
        foreach (string argument in start.Arguments)
            info.ArgumentList.Add(argument);
        try
        {
            Process process = Process.Start(info) ?? throw new Win32Exception();
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                start.CleanUp();
                process.Dispose();
            };
            return process;
        }
        catch
        {
            start.CleanUp();
            throw;
        }
    }

    private static string SafeFileName(string name)
    {
        string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return safe.Length > 0 ? safe : "Verbindung";
    }
}

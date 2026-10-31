using System.Text;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.Core.RemoteTools;

/// <summary>
/// A built command line. The secret is never part of <see cref="Arguments"/>: it either goes through
/// stdin (<see cref="SecretViaStdin"/>) or fills the slot <see cref="SecretSlot"/> only at start time
/// (<see cref="ArgumentsWith"/>). <see cref="Preview"/> shows the slot masked.
/// </summary>
public sealed class RemoteToolCommand
{
    public const string Mask = "••••••";

    internal RemoteToolCommand(string fileName, List<string> arguments, int secretSlot, bool secretViaStdin)
    {
        FileName = fileName;
        Arguments = arguments;
        SecretSlot = secretSlot;
        SecretViaStdin = secretViaStdin;
    }

    public string FileName { get; }

    /// <summary>The arguments; at <see cref="SecretSlot"/> stands <see cref="Mask"/>.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Index of the password argument, or -1 when the password is not on the command line.</summary>
    public int SecretSlot { get; }

    /// <summary>The password is written to the tool's stdin (AnyDesk <c>--with-password</c>).</summary>
    public bool SecretViaStdin { get; }

    /// <summary>True when the password would be visible in the process list (TeamViewer <c>-p</c>, RustDesk <c>--password</c>).</summary>
    public bool ExposesSecret => SecretSlot >= 0;

    /// <summary>The arguments to start with; the secret slot gets <paramref name="secret"/>.</summary>
    public IEnumerable<string> ArgumentsWith(string? secret) =>
        Arguments.Select((a, i) => i == SecretSlot ? secret ?? "" : a);

    /// <summary>The full call as it would be typed, password masked. For display only, never for starting.</summary>
    public string Preview => string.Join(' ', Arguments.Prepend(FileName).Select(RemoteToolCommandLine.Quote));
}

/// <summary>
/// Builds TeamViewer, AnyDesk, and RustDesk command lines from a connection's settings. Pure (no
/// process, no file system), so the rules are unit-tested. The app starts the result through
/// <c>ProcessStartInfo.ArgumentList</c>, never through a shell.
/// </summary>
public static class RemoteToolCommandLine
{
    /// <summary>Placeholder in <see cref="RemoteToolSettings.ExtraArguments"/> for the remote ID.</summary>
    public const string IdPlaceholder = "{id}";

    private static readonly string[] SecretSwitches = ["-p", "--password", "-b", "--passwordb64", "--with-password"];

    public static bool IsRemoteTool(ConnectionType type) =>
        type is ConnectionType.TeamViewer or ConnectionType.AnyDesk or ConnectionType.RustDesk;

    /// <summary>The modes a tool supports, in the order the editor lists them.</summary>
    public static IReadOnlyList<RemoteToolMode> ModesOf(ConnectionType type) => type switch
    {
        ConnectionType.TeamViewer => [RemoteToolMode.Control, RemoteToolMode.FileTransfer, RemoteToolMode.Vpn],
        ConnectionType.AnyDesk => [RemoteToolMode.Control, RemoteToolMode.FileTransfer],
        ConnectionType.RustDesk => [RemoteToolMode.Control, RemoteToolMode.FileTransfer, RemoteToolMode.PortForward],
        _ => [],
    };

    /// <summary>The ID as the tool expects it: blanks removed ("123 456 789" → "123456789").</summary>
    public static string NormalizeId(string host) =>
        new([.. host.Where(c => !char.IsWhiteSpace(c))]);

    /// <summary>
    /// Whether <paramref name="id"/> (normalized) is safe to pass: TeamViewer only digits; AnyDesk digits or
    /// an alias; RustDesk letters, digits and <c>-_.@:</c>. Never starts with '-', so it cannot pass for a switch.
    /// </summary>
    public static bool IsValidId(ConnectionType type, string id)
    {
        if (id.Length is 0 or > 128 || id[0] == '-')
            return false;
        return type switch
        {
            ConnectionType.TeamViewer => id.All(char.IsAsciiDigit),
            ConnectionType.AnyDesk => id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@'),
            ConnectionType.RustDesk => id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@' or ':'),
            _ => false,
        };
    }

    /// <summary>
    /// Builds the call. <paramref name="hasPassword"/>: a password is stored (it is added via stdin for
    /// AnyDesk, as a masked slot for TeamViewer/RustDesk when <see cref="RemoteToolSettings.CredentialOnCommandLine"/>).
    /// <paramref name="mode"/> overrides the stored mode (context menu "Dateiübertragung").
    /// </summary>
    public static RemoteToolCommand Build(ConnectionType type, string toolPath, string remoteId, RemoteToolSettings settings,
        bool hasPassword, RemoteToolMode? mode = null)
    {
        string id = NormalizeId(remoteId);
        RemoteToolMode effective = mode ?? settings.Mode;
        if (!ModesOf(type).Contains(effective))
            effective = RemoteToolMode.Control;

        var args = new List<string>();
        int slot = -1;
        bool stdin = false;
        bool passOnLine = hasPassword && settings.CredentialOnCommandLine;
        switch (type)
        {
            case ConnectionType.TeamViewer:
                args.AddRange(["-i", id]);
                if (passOnLine)
                {
                    args.Add("-p");
                    slot = args.Count;
                    args.Add(RemoteToolCommand.Mask);
                }

                if (effective == RemoteToolMode.FileTransfer)
                    args.AddRange(["-m", "fileTransfer"]);
                else if (effective == RemoteToolMode.Vpn)
                    args.AddRange(["-m", "vpn"]);
                if (settings.Quality is >= 1 and <= 5)
                    args.AddRange(["-q", settings.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                if (settings.AccessControl is 0 or 1 or 2 or 3 or 9)
                    args.AddRange(["-a", settings.AccessControl.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                break;

            case ConnectionType.AnyDesk:
                args.Add(id);
                if (effective == RemoteToolMode.FileTransfer)
                    args.Add("--file-transfer");
                if (settings.FullScreen)
                    args.Add("--full-screen");
                if (settings.Plain)
                    args.Add("--plain");
                break;

            case ConnectionType.RustDesk:
                args.Add(effective switch
                {
                    RemoteToolMode.FileTransfer => "--file-transfer",
                    RemoteToolMode.PortForward => "--port-forward",
                    _ => "--connect",
                });
                args.Add(id);
                if (passOnLine)
                {
                    args.Add("--password");
                    slot = args.Count;
                    args.Add(RemoteToolCommand.Mask);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Not a remote tool.");
        }

        args.AddRange(SplitArguments(settings.ExtraArguments).Select(a => a.Replace(IdPlaceholder, id, StringComparison.OrdinalIgnoreCase)));

        // AnyDesk reads the password from stdin; the switch goes last, after any extra arguments.
        if (type == ConnectionType.AnyDesk && hasPassword)
        {
            args.Add("--with-password");
            stdin = true;
        }

        return new RemoteToolCommand(toolPath, args, slot, stdin);
    }

    /// <summary>The first argument that would put a password on the command line, or null.</summary>
    public static string? FindSecretArgument(IEnumerable<string> arguments) =>
        arguments.FirstOrDefault(a =>
        {
            string name = a.Split('=', 2)[0];
            return SecretSwitches.Contains(name, StringComparer.OrdinalIgnoreCase);
        });

    /// <summary>Splits like a Windows command line: blanks separate, double quotes group, <c>\"</c> is a quote.</summary>
    public static IReadOnlyList<string> SplitArguments(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var current = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '"')
            {
                current.Append('"');
                any = true;
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                    result.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
            result.Add(current.ToString());
        return result;
    }

    /// <summary>Quotes an argument for display when it is empty or holds blanks or quotes.</summary>
    public static string Quote(string argument) =>
        argument.Length == 0 || argument.Any(c => char.IsWhiteSpace(c) || c == '"')
            ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : argument;
}

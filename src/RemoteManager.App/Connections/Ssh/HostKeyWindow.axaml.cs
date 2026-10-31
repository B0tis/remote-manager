using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.Core.Ssh;

namespace RemoteManager.App.Connections.Ssh;

/// <summary>
/// Asks whether to trust a server's SSH host key: on the first connect (unknown) and, with a warning,
/// when it differs from the stored one (changed). Cancel is the default button.
/// </summary>
public partial class HostKeyWindow : Window
{
    private bool accepted;

    public HostKeyWindow()
    {
        InitializeComponent();
        AcceptButton.Click += (_, _) => Accept();
        CancelButton.Click += (_, _) => Close();
    }

    /// <summary>The prompt while it is open (used by the self-test).</summary>
    public static HostKeyWindow? Current { get; private set; }

    public HostKeyStatus Status { get; private set; }

    /// <summary>Shows the prompt for <paramref name="problem"/>; true when the user trusts the key.</summary>
    public static async Task<bool> AskAsync(Window owner, SshHostKeyException problem)
    {
        var window = new HostKeyWindow();
        window.Show(problem);
        Current = window;
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            Current = null;
        }

        return window.accepted;
    }

    public void Accept()
    {
        accepted = true;
        Close();
    }

    private void Show(SshHostKeyException problem)
    {
        Status = problem.Status;
        bool changed = problem.Status == HostKeyStatus.Changed;
        SshLogin login = problem.Login;
        string server = login.Name.Length > 0 && login.Name != login.Host ? $"{login.Name} ({login.Host}:{login.Port})" : $"{login.Host}:{login.Port}";

        Title = changed ? Strings.HostKey_ChangedTitle : Strings.HostKey_WindowTitle;
        Heading.Text = changed ? Strings.HostKey_ChangedTitle : Strings.HostKey_UnknownTitle;
        Explanation.Text = string.Format(CultureInfo.CurrentCulture, changed ? Strings.HostKey_ChangedText : Strings.HostKey_UnknownText, server);
        Advice.Text = changed ? Strings.HostKey_ChangedAdvice : Strings.HostKey_UnknownAdvice;
        ServerText.Text = server;
        AlgorithmText.Text = problem.Presented.KeyLength > 0
            ? $"{problem.Presented.Algorithm} ({problem.Presented.KeyLength} Bit)"
            : problem.Presented.Algorithm;
        FingerprintBox.Text = problem.Presented.Sha256Fingerprint;
        StoredLabel.IsVisible = StoredBox.IsVisible = changed;
        StoredBox.Text = login.KnownHostKey;

        AcceptButton.Content = changed ? Strings.HostKey_AcceptChanged : Strings.HostKey_Accept;
        if (changed)
        {
            HeadingIcon.Symbol = FASymbol.ImportantFilled;
            HeadingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            // A changed key is the dangerous case: no accent on "accept".
            AcceptButton.Classes.Remove("accent");
        }
    }
}

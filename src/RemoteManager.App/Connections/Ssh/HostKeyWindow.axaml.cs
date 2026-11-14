using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input.Platform;
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

    /// <summary>Which of the two pictures shows, and how the buttons are weighted (self-test).</summary>
    internal bool ShowsUnknownPicture => UnknownHeader.IsVisible && FingerprintText.IsVisible && !CompareCard.IsVisible;
    internal bool ShowsChangedPicture => ChangedHeader.IsVisible && CompareCard.IsVisible && !FingerprintText.IsVisible;
    internal bool CancelIsDefault => CancelButton.IsDefault && !AcceptButton.IsDefault;
    internal bool AcceptIsRecommended => AcceptButton.Classes.Contains("accent");
    internal (string? Known, string? New) ComparedFingerprints => (StoredText.Text, NewText.Text);

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
        string fingerprint = problem.Presented.Sha256Fingerprint;

        Title = changed ? Strings.HostKey_ChangedTitle : Strings.HostKey_WindowTitle;
        ServerText.Text = server;
        AlgorithmText.Text = problem.Presented.KeyLength > 0
            ? $"{problem.Presented.Algorithm} ({problem.Presented.KeyLength} Bit)"
            : problem.Presented.Algorithm;

        // Two different pictures: a question with one fingerprint to check, or an alarm with the known and the
        // new fingerprint under each other.
        UnknownHeader.IsVisible = !changed;
        ChangedHeader.IsVisible = CompareCard.IsVisible = changed;
        FingerprintLabel.IsVisible = FingerprintText.IsVisible = CopyButton.IsVisible = !changed;
        UnknownText.Text = string.Format(CultureInfo.CurrentCulture, Strings.HostKey_UnknownText, server);
        ChangedText.Text = string.Format(CultureInfo.CurrentCulture, Strings.HostKey_ChangedText, server);
        FingerprintText.Text = NewText.Text = fingerprint;
        StoredText.Text = login.KnownHostKey;

        Advice.Text = changed ? Strings.HostKey_ChangedAdvice : Strings.HostKey_UnknownAdvice;
        AdviceCommand.Text = Strings.HostKey_UnknownCommand;
        AdviceCommand.IsVisible = !changed;

        AcceptButton.Content = changed ? Strings.HostKey_AcceptChanged : Strings.HostKey_Accept;
        // A changed key is the dangerous case: accepting it is no recommended answer, so it loses the accent.
        AcceptButton.Classes.Set("accent", !changed);
        AcceptButton.Classes.Set("danger", changed);
        CopyButton.Click += async (_, _) => await CopyAsync(fingerprint);
        CopyNewButton.Click += async (_, _) => await CopyAsync(fingerprint);
    }

    private async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}

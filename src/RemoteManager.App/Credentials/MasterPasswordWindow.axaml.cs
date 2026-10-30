using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using RemoteManager.App.Resources;
using RemoteManager.Core.Credentials;

namespace RemoteManager.App.Credentials;

/// <summary>Asks for a KeePass master password. The text is handed to the source and cleared; nothing is stored.</summary>
public partial class MasterPasswordWindow : Window
{
    public MasterPasswordWindow() : this(new MasterPasswordRequest("", null, false), TimeSpan.Zero)
    {
    }

    public MasterPasswordWindow(MasterPasswordRequest request, TimeSpan keyLifetime)
    {
        InitializeComponent();
        DatabaseText.Text = Path.GetFileName(request.DatabasePath);
        ToolTip.SetTip(DatabaseText, request.DatabasePath);
        KeyFileText.IsVisible = request.KeyFilePath is not null;
        KeyFileText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Unlock_KeyFile, Path.GetFileName(request.KeyFilePath));
        ErrorText.IsVisible = request.PreviousAttemptFailed;
        HintText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Unlock_Hint, keyLifetime.TotalMinutes);

        UnlockButton.Click += (_, _) => Unlock();
        CancelButton.Click += (_, _) => Close(null);
        Opened += (_, _) => PasswordBox.Focus();
    }

    /// <summary>The dialog while open (used by the self-test).</summary>
    public static MasterPasswordWindow? Current { get; private set; }

    public TextBox Password => PasswordBox;
    public bool ShowsWrongPassword => ErrorText.IsVisible;

    public void Unlock()
    {
        string password = PasswordBox.Text ?? "";
        PasswordBox.Text = "";
        Close(password);
    }

    /// <summary>Shows the dialog above the active window; returns the password or null (canceled).</summary>
    internal static Task<string?> AskAsync(MasterPasswordRequest request, TimeSpan keyLifetime) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = new MasterPasswordWindow(request, keyLifetime);
            Current = window;
            try
            {
                return await window.ShowDialog<string?>(OwnerWindow());
            }
            finally
            {
                Current = null;
            }
        });

    private static Window OwnerWindow()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
        return desktop.Windows.FirstOrDefault(w => w.IsActive && w is not MasterPasswordWindow)
            ?? desktop.MainWindow!;
    }
}

/// <summary>The app's <see cref="IMasterPasswordPrompt"/> for <see cref="KeePassCredentialSource"/>.</summary>
public sealed class MasterPasswordPrompt(KeePassCredentialSource source) : IMasterPasswordPrompt
{
    public async ValueTask<string?> AskAsync(MasterPasswordRequest request, CancellationToken cancellationToken) =>
        await MasterPasswordWindow.AskAsync(request, source.KeyLifetime);
}

using System.ComponentModel;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace RemoteManager.App;

/// <summary>
/// Form for the add/edit dialog. The main window shows it in an FAContentDialog.
/// The stored password is never shown: the dialog only knows whether one exists and takes a new one.
/// </summary>
public partial class ConnectionEditor : UserControl
{
    private bool hasStoredPassword;

    public ConnectionEditor()
    {
        InitializeComponent();
        TypeBox.SelectionChanged += (_, _) => UpdateTypeFields();
        SavePasswordBox.IsCheckedChanged += (_, _) => UpdatePasswordState();
        PasswordBox.TextChanged += (_, _) =>
        {
            // Typing a password here only makes sense to save it.
            if (PasswordBox.Text?.Length > 0)
                SavePasswordBox.IsChecked = true;
        };
        RemovePasswordButton.Click += (_, _) =>
        {
            PasswordBox.Text = "";
            SavePasswordBox.IsChecked = false;
        };
    }

    /// <summary>The dialog and its form while open (used by the self-test).</summary>
    public static FAContentDialog? OpenDialog { get; private set; }
    public static ConnectionEditor? OpenEditor { get; private set; }

    public TextBox Password => PasswordBox;
    public CheckBox SavePassword => SavePasswordBox;
    public Button RemovePassword => RemovePasswordButton;
    public bool IsStoredPasswordShown => StoredRow.IsVisible && SavePasswordBox.IsChecked == true;

    /// <summary>Shows the dialog. Returns true when the user saved; <paramref name="connection"/> is then updated.</summary>
    public static async Task<bool> ShowAsync(Connection connection, bool isNew, IEnumerable<string> groups)
    {
        var editor = new ConnectionEditor();
        editor.Load(connection, groups);

        var dialog = new FAContentDialog
        {
            Title = isNew ? "New connection" : $"Edit {connection.Name}",
            Content = editor,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, e) => e.Cancel = !editor.TryCommit(connection);
        dialog.Opened += (_, _) => editor.NameBox.Focus();

        OpenDialog = dialog;
        OpenEditor = editor;
        FAContentDialogResult result = await dialog.ShowAsync();
        OpenDialog = null;
        OpenEditor = null;
        return result == FAContentDialogResult.Primary;
    }

    /// <summary>Validates, applies the password change to the Credential Manager, then updates <paramref name="c"/>.</summary>
    public bool TryCommit(Connection c)
    {
        if (!Validate())
            return false;

        try
        {
            string password = PasswordBox.Text ?? "";
            if (SavePasswordBox.IsChecked != true)
                CredentialStore.Delete(c.Id);
            else if (password.Length > 0)
                CredentialStore.Write(c.Id, password);
        }
        catch (Win32Exception ex)
        {
            ShowError($"Passwort konnte nicht gespeichert werden: {ex.Message}");
            return false;
        }

        PasswordBox.Text = "";
        Store(c);
        c.RefreshCredentialState();
        return true;
    }

    private void Load(Connection c, IEnumerable<string> groups)
    {
        NameBox.Text = c.Name;
        TypeBox.SelectedIndex = (int)c.Type;
        HostBox.Text = c.Host;
        PortBox.Text = c.Port > 0 ? c.Port.ToString() : "";
        DescriptionBox.Text = c.Description;
        GroupBox.ItemsSource = groups.ToList();
        GroupBox.Text = c.Group;
        TagsBox.Text = string.Join(", ", c.Tags);
        UserBox.Text = c.UserName;
        DomainBox.Text = c.Domain;
        DisplayBox.SelectedIndex = c.DisplayMode;
        WinKeysBox.IsChecked = c.WinKeysToSession;
        FavoriteBox.IsChecked = c.IsFavorite;
        hasStoredPassword = CredentialStore.Exists(c.Id);
        SavePasswordBox.IsChecked = hasStoredPassword;
        UpdateTypeFields();
        UpdatePasswordState();
    }

    private bool Validate()
    {
        string? error =
            string.IsNullOrWhiteSpace(NameBox.Text) ? "Name is required." :
            string.IsNullOrWhiteSpace(HostBox.Text) ? (IsRdp ? "Host is required." : "Remote ID is required.") :
            IsRdp && PortBox.Text is { Length: > 0 } port && !(int.TryParse(port, out int p) && p is > 0 and < 65536) ? "Port must be between 1 and 65535." :
            SavePasswordBox.IsChecked == true && !hasStoredPassword && string.IsNullOrEmpty(PasswordBox.Text) ? "Passwort eingeben oder „Passwort speichern“ ausschalten." :
            null;

        ShowError(error);
        return error is null;
    }

    private void ShowError(string? error)
    {
        ErrorText.Text = error;
        ErrorText.IsVisible = error is not null;
    }

    private void Store(Connection c)
    {
        c.Name = NameBox.Text!.Trim();
        c.Type = (ConnectionType)TypeBox.SelectedIndex;
        c.Host = HostBox.Text!.Trim();
        c.Port = IsRdp ? (int.TryParse(PortBox.Text, out int port) ? port : 3389) : 0;
        c.Description = DescriptionBox.Text?.Trim() ?? "";
        c.Group = GroupBox.Text?.Trim() ?? "";
        c.Tags = [.. (TagsBox.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];
        c.UserName = UserBox.Text?.Trim() ?? "";
        c.Domain = DomainBox.Text?.Trim() ?? "";
        c.DisplayMode = Math.Max(0, DisplayBox.SelectedIndex);
        c.WinKeysToSession = WinKeysBox.IsChecked == true;
        c.IsFavorite = FavoriteBox.IsChecked == true;
        c.Changed();
    }

    private bool IsRdp => TypeBox.SelectedIndex == (int)ConnectionType.Rdp;

    private void UpdatePasswordState()
    {
        bool save = SavePasswordBox.IsChecked == true;
        StoredRow.IsVisible = hasStoredPassword;
        StoredText.Text = save ? "Passwort gespeichert" : "Gespeichertes Passwort wird beim Speichern entfernt";
        StoredIcon.Opacity = save ? 1 : 0.4;
        RemovePasswordButton.IsVisible = save;
        PasswordBox.PlaceholderText = hasStoredPassword ? "Neues Passwort (leer = behalten)" : "Passwort";
        UpdatePasswordHint();
    }

    private void UpdatePasswordHint() =>
        PasswordHint.Text = IsRdp
            ? "Gespeichert im Windows Credential Manager. Mit Passwort meldet sich die Sitzung direkt an (NLA); ohne wird es beim Verbinden abgefragt."
            : "Gespeichert im Windows Credential Manager. „Passwort kopieren“ im Kontextmenü legt es für 30 s in die Zwischenablage.";

    private void UpdateTypeFields()
    {
        UserRow.IsVisible = IsRdp;
        RdpOptions.IsVisible = IsRdp;
        PortBox.IsVisible = IsRdp;
        RemoteToolHint.IsVisible = !IsRdp;
        HostBox.PlaceholderText = IsRdp ? "Host or IP" : "Remote ID or alias";
        UpdatePasswordHint();
    }
}

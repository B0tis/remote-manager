using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections;
using RemoteManager.App.Credentials;
using RemoteManager.App.Resources;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Views;

/// <summary>
/// Form for the add/edit dialog. The main window shows it in an FAContentDialog. The fields and the
/// type-specific part come from the type's <see cref="IConnectionHandler"/>, so a new type needs no change here.
/// The stored password is never shown: the dialog only knows whether one exists and takes a new one.
/// </summary>
public partial class ConnectionEditor : UserControl
{
    private readonly AppHost app;
    private readonly List<IConnectionHandler> handlers;
    private Connection? original;
    private ISettingsEditor? settingsEditor;
    private bool hasStoredPassword;

    public ConnectionEditor()
    {
        InitializeComponent();
        app = AppHost.Current;
        handlers = [.. app.Handlers.All];
        TypeBox.ItemsSource = handlers.Select(h => h.DisplayName).ToList();

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
        UserBox.TextChanged += (_, _) => UpdateInheritedHint();
        SourcePanel.KindChanged += (_, _) =>
        {
            OwnPasswordPanel.IsVisible = !SourcePanel.IsExternal;
            UpdateInheritedHint();
        };
    }

    /// <summary>The dialog and its form while open (used by the self-test).</summary>
    public static FAContentDialog? OpenDialog { get; private set; }
    public static ConnectionEditor? OpenEditor { get; private set; }

    public TextBox Password => PasswordBox;
    public CheckBox SavePassword => SavePasswordBox;
    public Button RemovePassword => RemovePasswordButton;
    public PasswordSourcePanel Source => SourcePanel;
    public bool IsStoredPasswordShown => StoredRow.IsVisible && SavePasswordBox.IsChecked == true;

    /// <summary>Shows the dialog. Returns true when the user saved; <paramref name="connection"/> is then updated (not yet persisted).</summary>
    public static async Task<bool> ShowAsync(Connection connection, bool isNew)
    {
        var editor = new ConnectionEditor();
        editor.Load(connection);

        var dialog = new FAContentDialog
        {
            Title = isNew ? Strings.Editor_TitleNew : string.Format(CultureInfo.CurrentCulture, Strings.Editor_TitleEdit, connection.Name),
            Content = editor,
            PrimaryButtonText = Strings.Action_Save,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            e.Cancel = !await editor.TryCommitAsync(connection);
            deferral.Complete();
        };
        dialog.Opened += (_, _) => editor.NameBox.Focus();

        OpenDialog = dialog;
        OpenEditor = editor;
        FAContentDialogResult result = await dialog.ShowAsync();
        OpenDialog = null;
        OpenEditor = null;
        return result == FAContentDialogResult.Primary;
    }

    /// <summary>Validates, applies the password change to the credential store, then updates <paramref name="c"/>.</summary>
    public async Task<bool> TryCommitAsync(Connection c)
    {
        if (!Validate())
            return false;

        try
        {
            string password = PasswordBox.Text ?? "";
            if (SourcePanel.BuildReference() is { } reference)
            {
                // KeePass / 1Password: only the reference is stored; the secret is fetched at connect time.
                await app.Credentials.UseReferenceAsync(c, reference);
            }
            else if (SavePasswordBox.IsChecked != true)
            {
                if (c.Credential is not null || hasStoredPassword)
                    await app.Credentials.RemoveOwnSecretAsync(c);
            }
            else if (password.Length > 0)
            {
                await app.Credentials.SaveOwnSecretAsync(c, password);
            }
        }
        catch (Win32Exception ex)
        {
            ShowError(string.Format(CultureInfo.CurrentCulture, Strings.Password_SaveFailed, ex.Message));
            return false;
        }

        PasswordBox.Text = "";
        Store(c);
        return true;
    }

    private IConnectionHandler Handler => handlers[Math.Max(0, TypeBox.SelectedIndex)];

    private void Load(Connection c)
    {
        original = c;
        NameBox.Text = c.Name;
        TypeBox.SelectedIndex = Math.Max(0, handlers.FindIndex(h => h.Type == c.Type));
        HostBox.Text = c.Host;
        PortBox.Text = c.Port > 0 ? c.Port.ToString(CultureInfo.InvariantCulture) : "";
        DescriptionBox.Text = c.Description;
        NotesBox.Text = c.Notes;
        GroupBox.ItemsSource = app.Library.GroupPaths().ToList();
        GroupBox.Text = app.Library.PathOf(c.GroupId);
        TagsBox.Text = string.Join(", ", c.Tags);
        UserBox.Text = c.UserName;
        DomainBox.Text = c.Domain;
        FavoriteBox.IsChecked = c.IsFavorite;
        // Only a password the app stored itself counts here; a KeePass/1Password reference is the source panel's.
        hasStoredPassword = c.Credential is not null && c.Credential.Source == app.Credentials.DefaultSource.Id;
        SourcePanel.Load(c.Credential);
        SavePasswordBox.IsChecked = hasStoredPassword;
        UpdateTypeFields();
        UpdatePasswordState();
    }

    private bool Validate()
    {
        string? error =
            string.IsNullOrWhiteSpace(NameBox.Text) ? Strings.Editor_Error_Name :
            string.IsNullOrWhiteSpace(HostBox.Text) ? (Handler.HasPort ? Strings.Editor_Error_Host : Strings.Editor_Error_RemoteId) :
            Handler.HasPort && PortBox.Text is { Length: > 0 } port && !(int.TryParse(port, out int p) && p is > 0 and < 65536) ? Strings.Editor_Error_Port :
            SourcePanel.IsExternal ? SourcePanel.Validate() ?? settingsEditor?.Validate() :
            SavePasswordBox.IsChecked == true && !hasStoredPassword && string.IsNullOrEmpty(PasswordBox.Text) ? Strings.Editor_Error_Password :
            settingsEditor?.Validate();

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
        IConnectionHandler handler = Handler;
        c.Name = NameBox.Text!.Trim();
        c.Type = handler.Type;
        c.Host = HostBox.Text!.Trim();
        c.Port = handler.HasPort && int.TryParse(PortBox.Text, out int port) ? port : 0;
        c.Description = DescriptionBox.Text?.Trim() ?? "";
        c.Notes = NotesBox.Text?.Trim() ?? "";
        c.GroupId = app.Library.EnsureGroupPath(GroupBox.Text)?.Id;
        c.Tags = [.. (TagsBox.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
        c.UserName = handler.UsesUserName ? UserBox.Text?.Trim() ?? "" : "";
        c.Domain = handler.UsesUserName ? DomainBox.Text?.Trim() ?? "" : "";
        c.IsFavorite = FavoriteBox.IsChecked == true;
        if (settingsEditor is not null)
            c.Settings = settingsEditor.Commit();
    }

    private void UpdatePasswordState()
    {
        bool save = SavePasswordBox.IsChecked == true;
        StoredRow.IsVisible = hasStoredPassword;
        StoredText.Text = save ? Strings.Password_Stored : Strings.Password_WillBeRemoved;
        StoredIcon.Opacity = save ? 1 : 0.4;
        RemovePasswordButton.IsVisible = save;
        PasswordBox.PlaceholderText = hasStoredPassword ? Strings.Password_NewPlaceholder : Strings.Field_Password;
        PasswordHint.Text = Handler.PasswordHint;
    }

    /// <summary>Shows which group's logon applies while the connection has no user and no password of its own.</summary>
    private void UpdateInheritedHint()
    {
        EffectiveCredential inherited = original is null
            ? EffectiveCredential.None
            : app.Credentials.Resolve(new Connection { GroupId = app.Library.FindGroup(original.GroupId)?.Id });
        bool own = UserBox.Text?.Length > 0 || hasStoredPassword || SourcePanel.IsExternal;
        InheritedHint.IsVisible = !own && inherited.IsInherited;
        if (InheritedHint.IsVisible)
        {
            string user = inherited.Domain.Length > 0 ? $"{inherited.Domain}\\{inherited.UserName}" : inherited.UserName;
            InheritedHint.Text = string.Format(CultureInfo.CurrentCulture, Strings.Editor_InheritedLogon,
                app.Library.PathOf(inherited.InheritedFrom!.Id), user.Length > 0 ? user : "-",
                inherited.HasSecret ? Strings.Editor_InheritedWithPassword : "");
        }
    }

    private void UpdateTypeFields()
    {
        IConnectionHandler handler = Handler;
        UserRow.IsVisible = handler.UsesUserName;
        PortBox.IsVisible = handler.HasPort;
        PortBox.PlaceholderText = string.Format(CultureInfo.CurrentCulture, Strings.Editor_PortPlaceholder, DefaultPorts.For(handler.Type));
        HostBox.PlaceholderText = handler.HostPlaceholder;
        PasswordHint.Text = handler.PasswordHint;

        settingsEditor = handler.CreateSettingsEditor();
        if (settingsEditor is not null)
        {
            settingsEditor.Load(original is not null && ConnectionSettings.Fits(handler.Type, original.Settings)
                ? original.Settings
                : ConnectionSettings.CreateDefault(handler.Type));
        }

        SettingsHost.Content = settingsEditor?.View;
        UpdateInheritedHint();
    }
}

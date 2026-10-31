using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using RemoteManager.App.Resources;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Credentials.KeePass;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Credentials;

/// <summary>Where a connection's password lives.</summary>
public enum PasswordSourceKind { App, KeePass, OnePassword }

/// <summary>
/// The source choice of the connection editor: the app's own password (Credential Manager, the editor's
/// password box), a KeePass entry, or a 1Password item. For the password managers only a
/// <see cref="CredentialRef"/> is built; "Prüfen" reads the secret once and drops it.
/// </summary>
public partial class PasswordSourcePanel : UserControl
{
    /// <summary>app_settings key: the KeePass database picked last, as the default for the next connection.</summary>
    public const string LastDatabaseSetting = "keepass.lastDatabase";

    private const string LastKeyFileSetting = "keepass.lastKeyFile";

    private readonly AppHost app;

    public PasswordSourcePanel()
    {
        InitializeComponent();
        app = AppHost.Current;
        KeePassHint.Text = string.Format(CultureInfo.CurrentCulture, Strings.Vault_KeePass_Hint, KeePass?.KeyLifetime.TotalMinutes ?? 0);

        SourceBox.SelectionChanged += (_, _) => UpdateKind();
        BrowseDatabaseButton.Click += async (_, _) => await BrowseAsync(DatabaseBox, Strings.Vault_FileFilter_KeePass, ["*.kdbx"]);
        BrowseKeyFileButton.Click += async (_, _) => await BrowseAsync(KeyFileBox, Strings.Vault_FileFilter_KeyFile, ["*.keyx", "*.key", "*.*"]);
        PickEntryButton.Click += async (_, _) => await PickEntryAsync();
        CheckButton.Click += async (_, _) => await CheckAsync();
        foreach (TextBox box in new[] { DatabaseBox, KeyFileBox, EntryBox, OpReferenceBox })
            box.TextChanged += (_, _) => ShowCheck(null, false);
    }

    /// <summary>Raised when the user switches the source (the editor shows or hides its password box).</summary>
    public event EventHandler? KindChanged;

    public PasswordSourceKind Kind => (PasswordSourceKind)Math.Max(0, SourceBox.SelectedIndex);

    /// <summary>KeePass or 1Password: the editor's own password box does not apply.</summary>
    public bool IsExternal => Kind != PasswordSourceKind.App;

    // For the self-test.
    public ComboBox Source => SourceBox;
    public TextBox Database => DatabaseBox;
    public TextBox KeyFile => KeyFileBox;
    public TextBox Entry => EntryBox;
    public TextBox OpReference => OpReferenceBox;
    public string CheckResult => CheckText.Text ?? "";
    public bool CheckSucceeded => CheckIcon.IsVisible;

    private KeePassCredentialSource? KeePass => app.Credentials.Sources.OfType<KeePassCredentialSource>().FirstOrDefault();
    private OnePasswordCredentialSource? OnePassword => app.Credentials.Sources.OfType<OnePasswordCredentialSource>().FirstOrDefault();

    /// <summary>Shows the source <paramref name="reference"/> points at (null or the Credential Manager: the app's own).</summary>
    public void Load(CredentialRef? reference)
    {
        DatabaseBox.Text = app.Library.GetSetting(LastDatabaseSetting) ?? "";
        KeyFileBox.Text = app.Library.GetSetting(LastKeyFileSetting) ?? "";
        EntryBox.Text = "";
        OpReferenceBox.Text = "";
        PasswordSourceKind kind = PasswordSourceKind.App;
        if (reference?.Source == KeePassCredentialSource.SourceId && KeePassReference.TryParse(reference.Key, out KeePassReference? keePass))
        {
            kind = PasswordSourceKind.KeePass;
            DatabaseBox.Text = keePass!.Database;
            KeyFileBox.Text = keePass.KeyFile ?? "";
            EntryBox.Text = keePass.Entry;
        }
        else if (reference?.Source == OnePasswordCredentialSource.SourceId)
        {
            kind = PasswordSourceKind.OnePassword;
            OpReferenceBox.Text = reference.Key;
        }

        SourceBox.SelectedIndex = (int)kind;
        UpdateKind();
    }

    /// <summary>German error for the editor, or null.</summary>
    public string? Validate() => Kind switch
    {
        PasswordSourceKind.KeePass when string.IsNullOrWhiteSpace(DatabaseBox.Text) => Strings.Vault_Error_DatabaseRequired,
        PasswordSourceKind.KeePass when string.IsNullOrWhiteSpace(EntryBox.Text) => Strings.Vault_Error_EntryRequired,
        PasswordSourceKind.OnePassword when !OnePasswordCredentialSource.IsValidReference(OpReferenceBox.Text ?? "") => Strings.Vault_Error_OpReference,
        _ => null,
    };

    /// <summary>The reference for KeePass or 1Password; null for the app's own password.</summary>
    public CredentialRef? BuildReference()
    {
        switch (Kind)
        {
            case PasswordSourceKind.KeePass:
                string database = DatabaseBox.Text!.Trim();
                string keyFile = KeyFileBox.Text?.Trim() ?? "";
                app.Library.SetSetting(LastDatabaseSetting, database);
                app.Library.SetSetting(LastKeyFileSetting, keyFile.Length > 0 ? keyFile : null);
                return new CredentialRef(KeePassCredentialSource.SourceId, new KeePassReference(database, EntryBox.Text!.Trim(), keyFile).ToKey());
            case PasswordSourceKind.OnePassword:
                return new CredentialRef(OnePasswordCredentialSource.SourceId, OpReferenceBox.Text!.Trim());
            default:
                return null;
        }
    }

    /// <summary>Reads the secret once to prove the reference works; the value is dropped right away.</summary>
    public async Task CheckAsync()
    {
        if (Validate() is { } error)
        {
            ShowCheck(error, false);
            return;
        }

        CredentialRef reference = BuildReference()!;
        CheckButton.IsEnabled = false;
        ShowCheck(Strings.Vault_Checking, false);
        try
        {
            string? secret = await app.Credentials.ReadAsync(reference);
            ShowCheck(secret is not null ? Strings.Vault_Check_Ok : string.Format(CultureInfo.CurrentCulture, Strings.Vault_Err_UnknownSource, reference.Source), secret is not null);
        }
        catch (Exception ex) when (CredentialErrors.IsCredentialFailure(ex))
        {
            ShowCheck(CredentialErrors.Describe(ex), false);
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    /// <summary>Unlocks the database (asks when needed) and lets the user pick the entry.</summary>
    public async Task PickEntryAsync()
    {
        if (KeePass is not { } source)
            return;
        if (string.IsNullOrWhiteSpace(DatabaseBox.Text))
        {
            ShowCheck(Strings.Vault_Error_DatabaseRequired, false);
            return;
        }

        string database = DatabaseBox.Text.Trim();
        string? keyFile = KeyFileBox.Text?.Trim() is { Length: > 0 } key ? key : null;
        try
        {
            IReadOnlyList<KdbxEntry> entries = await source.ReadEntriesAsync(database, keyFile);
            if (TopLevel.GetTopLevel(this) is Window owner && await KeePassEntryPicker.PickAsync(owner, database, entries) is { } picked)
                EntryBox.Text = picked;
        }
        catch (Exception ex) when (CredentialErrors.IsCredentialFailure(ex))
        {
            ShowCheck(CredentialErrors.Describe(ex), false);
        }
    }

    private async Task BrowseAsync(TextBox target, string filterName, string[] patterns)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
            return;
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            target.Text = path;
    }

    private void UpdateKind()
    {
        KeePassPanel.IsVisible = Kind == PasswordSourceKind.KeePass;
        OnePasswordPanel.IsVisible = Kind == PasswordSourceKind.OnePassword;
        OnePasswordMissing.IsVisible = Kind == PasswordSourceKind.OnePassword && OnePassword?.FindExecutable() is null;
        CheckRow.IsVisible = IsExternal;
        ShowCheck(null, false);
        KindChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowCheck(string? text, bool ok)
    {
        CheckText.Text = text;
        CheckIcon.IsVisible = ok;
    }
}

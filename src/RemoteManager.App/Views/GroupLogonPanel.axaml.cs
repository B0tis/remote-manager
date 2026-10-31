using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using RemoteManager.App.Credentials;
using RemoteManager.App.Resources;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Views;

/// <summary>
/// Edits the logon (user, domain, password) of a group; connections in it without their own logon
/// inherit it. The password goes to the Credential Manager ("group-&lt;id&gt;") and is never read back,
/// or the group points at a KeePass entry or 1Password item.
/// </summary>
public partial class GroupLogonPanel : UserControl
{
    private readonly ConnectionGroup group;
    private readonly bool hasStoredPassword;

    // Required by the XAML loader.
    public GroupLogonPanel() : this(new ConnectionGroup())
    {
    }

    public GroupLogonPanel(ConnectionGroup group)
    {
        InitializeComponent();
        this.group = group;
        TitleText.Text = string.Format(CultureInfo.CurrentCulture, Strings.GroupLogon_Title, AppHost.Current.Library.PathOf(group.Id));
        UserBox.Text = group.UserName;
        DomainBox.Text = group.Domain;
        hasStoredPassword = group.Credential?.Source == AppHost.Current.Credentials.DefaultSource.Id;
        PasswordBox.PlaceholderText = hasStoredPassword ? Strings.Password_NewPlaceholder : Strings.Field_Password;
        RemovePasswordBox.IsVisible = hasStoredPassword;
        SourcePanel.Load(group.Credential);
        OwnPasswordPanel.IsVisible = !SourcePanel.IsExternal;
        SourcePanel.KindChanged += (_, _) => OwnPasswordPanel.IsVisible = !SourcePanel.IsExternal;
        ApplyButton.Click += async (_, _) => await ApplyAsync();
    }

    /// <summary>Raised after the group was saved.</summary>
    public event EventHandler? Applied;

    public TextBox User => UserBox;
    public TextBox Password => PasswordBox;
    public PasswordSourcePanel Source => SourcePanel;

    public async Task<bool> ApplyAsync()
    {
        AppHost app = AppHost.Current;
        string password = PasswordBox.Text ?? "";
        if (SourcePanel.IsExternal && SourcePanel.Validate() is { } invalid)
        {
            ErrorText.Text = invalid;
            ErrorText.IsVisible = true;
            return false;
        }

        try
        {
            if (SourcePanel.BuildReference() is { } reference)
                await app.Credentials.UseGroupReferenceAsync(group, reference);
            else if (password.Length > 0)
                await app.Credentials.SaveGroupSecretAsync(group, password);
            // Remove the own password, or drop a password-manager reference when switched back to the app without a new one.
            else if (group.Credential is not null && (RemovePasswordBox.IsChecked == true || !hasStoredPassword))
                await app.Credentials.RemoveGroupSecretAsync(group);
        }
        catch (Win32Exception ex)
        {
            ErrorText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Password_SaveFailed, ex.Message);
            ErrorText.IsVisible = true;
            return false;
        }
        finally
        {
            PasswordBox.Text = "";
        }

        group.UserName = UserBox.Text?.Trim() ?? "";
        group.Domain = DomainBox.Text?.Trim() ?? "";
        group.UpdatedAt = DateTime.UtcNow;
        app.Library.UpdateGroup(group);
        Applied?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

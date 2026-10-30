using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using RemoteManager.App.Resources;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Views;

/// <summary>
/// Edits the logon (user, domain, password) of a group; connections in it without their own logon
/// inherit it. The password goes to the Credential Manager ("group-&lt;id&gt;") and is never read back.
/// </summary>
public partial class GroupLogonPanel : UserControl
{
    private readonly ConnectionGroup group;

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
        PasswordBox.PlaceholderText = group.Credential is null ? Strings.Field_Password : Strings.Password_NewPlaceholder;
        RemovePasswordBox.IsVisible = group.Credential is not null;
        ApplyButton.Click += async (_, _) => await ApplyAsync();
    }

    /// <summary>Raised after the group was saved.</summary>
    public event EventHandler? Applied;

    public TextBox User => UserBox;
    public TextBox Password => PasswordBox;

    public async Task<bool> ApplyAsync()
    {
        AppHost app = AppHost.Current;
        string password = PasswordBox.Text ?? "";
        try
        {
            if (password.Length > 0)
                await app.Credentials.SaveGroupSecretAsync(group, password);
            else if (RemovePasswordBox.IsChecked == true && group.Credential is not null)
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

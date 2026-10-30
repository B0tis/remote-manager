using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Views;

/// <summary>
/// Create or edit a group: name, parent, description, and the logon its connections inherit (user,
/// domain, password in the Credential Manager). The caller adds or moves the group in the library.
/// </summary>
public partial class GroupEditor : UserControl
{
    private readonly AppHost app;
    private readonly List<string?> parentIds = [];
    private bool hasStoredPassword;

    public GroupEditor()
    {
        InitializeComponent();
        app = AppHost.Current;
        SavePasswordBox.IsCheckedChanged += (_, _) => UpdatePasswordState();
        PasswordBox.TextChanged += (_, _) =>
        {
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
    public static GroupEditor? OpenEditor { get; private set; }

    public TextBox NameField => NameBox;
    public ComboBox ParentField => ParentBox;
    public TextBox UserField => UserBox;

    /// <summary>The parent chosen in the dialog (null: top level).</summary>
    public string? SelectedParentId => ParentBox.SelectedIndex >= 0 ? parentIds[ParentBox.SelectedIndex] : null;

    /// <summary>
    /// Shows the dialog. On save the group's name, description, and logon are updated (not yet
    /// persisted) and the chosen parent is returned; moving it there is up to the caller.
    /// </summary>
    public static async Task<(bool Saved, string? ParentId)> ShowAsync(ConnectionGroup group, bool isNew)
    {
        var editor = new GroupEditor();
        editor.Load(group);
        var dialog = new FAContentDialog
        {
            Title = isNew ? Strings.GroupEditor_TitleNew : string.Format(CultureInfo.CurrentCulture, Strings.GroupEditor_TitleEdit, group.Name),
            Content = editor,
            PrimaryButtonText = Strings.Action_Save,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            e.Cancel = !await editor.TryCommitAsync(group);
            deferral.Complete();
        };
        dialog.Opened += (_, _) => editor.NameBox.Focus();

        OpenDialog = dialog;
        OpenEditor = editor;
        FAContentDialogResult result = await dialog.ShowAsync();
        OpenDialog = null;
        OpenEditor = null;
        return (result == FAContentDialogResult.Primary, editor.SelectedParentId);
    }

    /// <summary>Validates, applies the password change, then updates <paramref name="group"/> (not its parent).</summary>
    public async Task<bool> TryCommitAsync(ConnectionGroup group)
    {
        string name = NameBox.Text?.Trim() ?? "";
        string? error =
            name.Length == 0 ? Strings.Editor_Error_Name :
            name.Contains('/', StringComparison.Ordinal) ? Strings.GroupEditor_Error_Slash :
            app.Library.HasSiblingNamed(SelectedParentId, name, except: group) ? Strings.GroupEditor_Error_Duplicate :
            SavePasswordBox.IsChecked == true && !hasStoredPassword && string.IsNullOrEmpty(PasswordBox.Text) ? Strings.Editor_Error_Password :
            null;
        ShowError(error);
        if (error is not null)
            return false;

        try
        {
            string password = PasswordBox.Text ?? "";
            if (SavePasswordBox.IsChecked != true)
            {
                if (group.Credential is not null)
                    await app.Credentials.RemoveGroupSecretAsync(group);
            }
            else if (password.Length > 0)
            {
                await app.Credentials.SaveGroupSecretAsync(group, password);
            }
        }
        catch (Win32Exception ex)
        {
            ShowError(string.Format(CultureInfo.CurrentCulture, Strings.Password_SaveFailed, ex.Message));
            return false;
        }

        PasswordBox.Text = "";
        group.Name = name;
        group.Description = DescriptionBox.Text?.Trim() ?? "";
        group.UserName = UserBox.Text?.Trim() ?? "";
        group.Domain = DomainBox.Text?.Trim() ?? "";
        return true;
    }

    private void Load(ConnectionGroup group)
    {
        ConnectionLibrary library = app.Library;
        NameBox.Text = group.Name;
        DescriptionBox.Text = group.Description;
        UserBox.Text = group.UserName;
        DomainBox.Text = group.Domain;

        // Any group except this one and its subgroups can be the parent.
        var names = new List<string> { Strings.GroupEditor_TopLevel };
        parentIds.Add(null);
        foreach (ConnectionGroup candidate in library.DescendantsOf(null).Where(g => g != group && library.CanMoveGroup(group, g.Id)))
        {
            parentIds.Add(candidate.Id);
            names.Add(library.PathOf(candidate.Id));
        }

        ParentBox.ItemsSource = names;
        ParentBox.SelectedIndex = Math.Max(0, parentIds.IndexOf(group.ParentId));
        hasStoredPassword = group.Credential is not null;
        SavePasswordBox.IsChecked = hasStoredPassword;
        UpdatePasswordState();
    }

    private void UpdatePasswordState()
    {
        bool save = SavePasswordBox.IsChecked == true;
        StoredRow.IsVisible = hasStoredPassword;
        StoredText.Text = save ? Strings.Password_Stored : Strings.Password_WillBeRemoved;
        StoredIcon.Opacity = save ? 1 : 0.4;
        RemovePasswordButton.IsVisible = save;
        PasswordBox.PlaceholderText = hasStoredPassword ? Strings.Password_NewPlaceholder : Strings.Field_Password;
    }

    private void ShowError(string? error)
    {
        ErrorText.Text = error;
        ErrorText.IsVisible = error is not null;
    }
}

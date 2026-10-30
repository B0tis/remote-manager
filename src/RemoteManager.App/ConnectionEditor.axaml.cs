using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace RemoteManager.App;

/// <summary>Form for the add/edit dialog. The main window shows it in an FAContentDialog.</summary>
public partial class ConnectionEditor : UserControl
{
    public ConnectionEditor()
    {
        InitializeComponent();
        TypeBox.SelectionChanged += (_, _) => UpdateTypeFields();
    }

    /// <summary>The dialog while it is open (used by the self-test).</summary>
    public static FAContentDialog? OpenDialog { get; private set; }

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
        dialog.PrimaryButtonClick += (_, e) => e.Cancel = !editor.Validate();
        dialog.Opened += (_, _) => editor.NameBox.Focus();

        OpenDialog = dialog;
        FAContentDialogResult result = await dialog.ShowAsync();
        OpenDialog = null;
        if (result != FAContentDialogResult.Primary)
            return false;

        editor.Store(connection);
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
        UpdateTypeFields();
    }

    private bool Validate()
    {
        string? error =
            string.IsNullOrWhiteSpace(NameBox.Text) ? "Name is required." :
            string.IsNullOrWhiteSpace(HostBox.Text) ? (IsRdp ? "Host is required." : "Remote ID is required.") :
            IsRdp && PortBox.Text is { Length: > 0 } port && !(int.TryParse(port, out int p) && p is > 0 and < 65536) ? "Port must be between 1 and 65535." :
            null;

        ErrorText.Text = error;
        ErrorText.IsVisible = error is not null;
        return error is null;
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

    private void UpdateTypeFields()
    {
        RdpOptions.IsVisible = IsRdp;
        PortBox.IsVisible = IsRdp;
        RemoteToolHint.IsVisible = !IsRdp;
        HostBox.PlaceholderText = IsRdp ? "Host or IP" : "Remote ID or alias";
    }
}

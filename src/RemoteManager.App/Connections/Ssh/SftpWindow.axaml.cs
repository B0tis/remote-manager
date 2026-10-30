using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteManager.App.Connections.Ssh;

/// <summary>
/// SFTP file browser: list, navigate, upload (button or drag and drop from Explorer), download, rename,
/// delete, new folder. Opened for SFTP connections and from the SSH terminal ("Dateien").
/// Network work runs off the UI thread; one operation at a time.
/// </summary>
public partial class SftpWindow : Window, ISessionWindow
{
    private SshRoute? route;
    private SftpClient? sftp;
    private string home = "/";
    private CancellationTokenSource? transferCancel;
    private bool busy;

    // Required by the XAML loader; the app always uses the ConnectionItem overload.
    public SftpWindow() : this(new ConnectionItem(new Connection { Name = "SFTP", Type = ConnectionType.Sftp }, AppHost.Current))
    {
    }

    /// <param name="knownLogin">A logon that already worked (from the SSH terminal), so the password is not asked again.</param>
    public SftpWindow(ConnectionItem connection, SshLogin? knownLogin = null)
    {
        InitializeComponent();
        Connection = connection;
        Title = string.Format(CultureInfo.CurrentCulture, Strings.Sftp_Title, connection.Name);
        Card.Load(connection, knownLogin);
        Card.ConnectRequested += (_, _) => Connect();
        Card.SecretSaved += (_, _) => PasswordSaved?.Invoke(this, EventArgs.Empty);
        ReconnectButton.Click += (_, _) => Connect();
        DisconnectButton.Click += (_, _) => Disconnect(Strings.Ssh_Status_Closed);

        UpButton.Click += (_, _) => _ = NavigateAsync(SftpTransfer.Parent(CurrentPath));
        HomeButton.Click += (_, _) => _ = NavigateAsync(home);
        RefreshButton.Click += (_, _) => _ = NavigateAsync(CurrentPath);
        PathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                _ = NavigateAsync(PathBox.Text?.Trim() is { Length: > 0 } path ? path : CurrentPath);
        };
        NewFolderButton.Click += (_, _) => _ = NewFolderAsync();
        UploadButton.Click += (_, _) => _ = PickAndUploadAsync();
        DownloadButton.Click += (_, _) => _ = PickAndDownloadAsync();
        RenameButton.Click += (_, _) => _ = RenameAsync();
        DeleteButton.Click += (_, _) => _ = DeleteAsync();
        CancelTransferButton.Click += (_, _) => transferCancel?.Cancel();

        MenuOpen.Click += (_, _) => OpenSelected();
        MenuDownload.Click += (_, _) => _ = PickAndDownloadAsync();
        MenuRename.Click += (_, _) => _ = RenameAsync();
        MenuDelete.Click += (_, _) => _ = DeleteAsync();
        MenuNewFolder.Click += (_, _) => _ = NewFolderAsync();
        MenuCopyPath.Click += (_, _) =>
        {
            if (Selected.FirstOrDefault() is { } item)
                _ = Clipboard?.SetTextAsync(item.Entry.FullName);
        };
        EntriesMenu.Opening += (_, _) =>
        {
            int count = Selected.Count;
            MenuOpen.IsVisible = count == 1;
            MenuDownload.IsVisible = MenuDelete.IsVisible = count > 0;
            MenuRename.IsVisible = MenuCopyPath.IsVisible = count == 1;
        };

        Entries.SelectionChanged += (_, _) => UpdateButtons();
        Entries.DoubleTapped += (_, _) => OpenSelected();
        Entries.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                OpenSelected();
            else if (e.Key == Key.Delete)
                _ = DeleteAsync();
            else if (e.Key == Key.Back)
                _ = NavigateAsync(SftpTransfer.Parent(CurrentPath));
        };

        DragDrop.AddDragEnterHandler(Entries, OnDragOver);
        DragDrop.AddDragOverHandler(Entries, OnDragOver);
        DragDrop.AddDragLeaveHandler(Entries, (_, _) => DropHint.IsVisible = false);
        DragDrop.AddDropHandler(Entries, OnDrop);

        Opened += (_, _) =>
        {
            if (Card.CanConnectWithoutInput)
                Connect();
            else
                Card.FocusInput();
        };
    }

    public ConnectionItem Connection { get; }

    public event EventHandler? SessionConnected;
    public event EventHandler? PasswordSaved;
    public event EventHandler<string>? StatusLogged;

    public bool IsConnected => sftp is { IsConnected: true };
    public bool IsBusy => busy;
    public string CurrentPath { get; private set; } = "";
    public IReadOnlyList<SftpItem> Items { get; private set; } = [];
    public string LastStatus => StatusText.Text ?? "";
    public bool IsCardVisible => Card.IsVisible;
    public SshLogonCard LogonCard => Card;
    public ListBox EntryList => Entries;

    private IReadOnlyList<SftpItem> Selected => [.. Entries.SelectedItems?.OfType<SftpItem>() ?? []];

    public void Connect() => _ = ConnectAsync();

    public async Task ConnectAsync()
    {
        if (Card.IsBusy || IsConnected)
            return;

        ReconnectButton.IsVisible = false;
        SetStatusDot(Color.FromRgb(0xF5, 0x9E, 0x0B));
        (SshRoute Route, SftpClient Client)? result = await Card.ConnectAsync(this, info => new SftpClient(info), SetStatus);
        if (result is null)
        {
            SetStatus(Card.MessageText.Length > 0 ? Card.MessageText : Strings.Session_NotConnected);
            SetStatusDot(Color.FromRgb(0x9C, 0xA3, 0xAF));
            ShowCard();
            return;
        }

        (route, sftp) = result.Value;
        sftp.ErrorOccurred += (_, e) => Dispatcher.UIThread.Post(() => Disconnect(SshConnect.Describe(e.Exception)));
        home = sftp.WorkingDirectory;
        Card.IsVisible = false;
        Browser.IsVisible = true;
        DisconnectButton.IsEnabled = true;
        SetStatusDot(Color.FromRgb(0x22, 0xC5, 0x5E));
        SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Ssh_Status_Connected, Card.LastLogin?.UserName, Connection.Model.Host));
        string start = Connection.Model.Settings is SshSettings { RemoteDirectory.Length: > 0 } settings ? settings.RemoteDirectory : home;
        if (!await NavigateAsync(start) && start != home)
            await NavigateAsync(home);
        Entries.Focus();
        SessionConnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lists <paramref name="path"/>; false (with a status message) when it cannot be read.</summary>
    public async Task<bool> NavigateAsync(string path)
    {
        if (sftp is not { } client || busy)
            return false;

        busy = true;
        try
        {
            // ChangeDirectory resolves "..", "~" and links on the server; WorkingDirectory is then the canonical path.
            string full = await Task.Run(() =>
            {
                client.ChangeDirectory(path);
                return client.WorkingDirectory;
            });
            IReadOnlyList<SftpEntry> entries = await Task.Run(() => SftpTransfer.List(client, full));
            CurrentPath = full;
            PathBox.Text = full;
            Items = [.. entries.Select(e => new SftpItem(e))];
            Entries.ItemsSource = Items;
            EmptyText.IsVisible = Items.Count == 0;
            UpButton.IsEnabled = full != "/";
            return true;
        }
        catch (Exception ex) when (IsSftpProblem(ex))
        {
            PathBox.Text = CurrentPath;
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Sftp_ListFailed, path, ex.Message));
            return false;
        }
        finally
        {
            busy = false;
            UpdateButtons();
        }
    }

    /// <summary>Uploads local files and folders into the current folder (asks before overwriting).</summary>
    public async Task UploadAsync(IReadOnlyList<string> localPaths)
    {
        if (sftp is not { } client || localPaths.Count == 0)
            return;

        var names = localPaths.Select(p => Path.GetFileName(p.TrimEnd('\\', '/'))).ToHashSet(StringComparer.Ordinal);
        List<string> existing = [.. Items.Where(i => names.Contains(i.Name)).Select(i => i.Name)];
        if (existing.Count > 0 && !await ConfirmAsync(Strings.Sftp_OverwriteTitle,
                string.Format(CultureInfo.CurrentCulture, Strings.Sftp_OverwriteMessage, string.Join(", ", existing.Take(5)) + (existing.Count > 5 ? ", …" : "")),
                Strings.Sftp_Overwrite))
            return;

        string target = CurrentPath;
        await RunTransferAsync(Strings.Sftp_Uploading, (progress, token) => SftpTransfer.Upload(client, localPaths, target, progress, token));
        await NavigateAsync(CurrentPath);
    }

    /// <summary>Downloads the given entries into <paramref name="localFolder"/>; returns the local paths.</summary>
    public async Task<IReadOnlyList<string>> DownloadAsync(IReadOnlyList<SftpItem> items, string localFolder)
    {
        if (sftp is not { } client || items.Count == 0)
            return [];

        IReadOnlyList<string> result = [];
        await RunTransferAsync(Strings.Sftp_Downloading, (progress, token) =>
            result = SftpTransfer.Download(client, [.. items.Select(i => i.Entry)], localFolder, progress, token));
        return result;
    }

    private async Task RunTransferAsync(string verb, Action<IProgress<SftpProgress>, CancellationToken> transfer)
    {
        if (busy)
            return;
        busy = true;
        UpdateButtons();
        using var cancel = new CancellationTokenSource();
        transferCancel = cancel;
        TransferBar.IsVisible = true;
        TransferProgress.Value = 0;
        TransferText.Text = verb;
        // Progress<T> posts to the UI thread it was created on.
        var progress = new Progress<SftpProgress>(p =>
        {
            TransferProgress.Value = p.Fraction;
            TransferText.Text = p.CurrentName.Length > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.Sftp_TransferProgress, verb, p.CurrentName, p.FileIndex + 1, p.FileCount, SftpItem.FormatSize(p.BytesDone), SftpItem.FormatSize(p.BytesTotal))
                : verb;
        });
        try
        {
            await Task.Run(() => transfer(progress, cancel.Token), CancellationToken.None);
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Sftp_TransferDone, verb));
        }
        catch (OperationCanceledException)
        {
            SetStatus(Strings.Sftp_TransferCanceled);
        }
        catch (Exception ex) when (IsSftpProblem(ex) || ex is UnauthorizedAccessException)
        {
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Sftp_TransferFailed, verb, ex.Message));
        }
        finally
        {
            transferCancel = null;
            TransferBar.IsVisible = false;
            busy = false;
            UpdateButtons();
        }
    }

    private async Task PickAndUploadAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Sftp_UploadPickerTitle,
            AllowMultiple = true,
        });
        await UploadAsync([.. files.Select(f => f.TryGetLocalPath()).OfType<string>()]);
    }

    private async Task PickAndDownloadAsync()
    {
        IReadOnlyList<SftpItem> items = Selected;
        if (items.Count == 0)
            return;
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Sftp_DownloadPickerTitle,
            SuggestedStartLocation = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads),
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder)
            await DownloadAsync(items, folder);
    }

    private void OpenSelected()
    {
        if (Selected is [{ } item])
        {
            if (item.Entry.IsDirectory)
                _ = NavigateAsync(item.Entry.FullName);
            else
                _ = PickAndDownloadAsync();
        }
    }

    private async Task NewFolderAsync()
    {
        if (sftp is not { } client || await AskNameAsync(Strings.Sftp_NewFolder, "", Strings.Sftp_Create) is not { } name)
            return;
        string path = SftpTransfer.Combine(CurrentPath, name);
        await RunOperationAsync(() => client.CreateDirectory(path));
    }

    private async Task RenameAsync()
    {
        if (sftp is not { } client || Selected is not [{ } item] || await AskNameAsync(Strings.Sftp_Rename, item.Name, Strings.Sftp_Rename) is not { } name || name == item.Name)
            return;
        string target = SftpTransfer.Combine(CurrentPath, name);
        await RunOperationAsync(() => client.RenameFile(item.Entry.FullName, target));
    }

    private Task DeleteAsync() => DeleteAsync(Selected, confirm: true);

    /// <summary>Deletes entries (folders with their content), after asking unless <paramref name="confirm"/> is false.</summary>
    public async Task DeleteAsync(IReadOnlyList<SftpItem> items, bool confirm)
    {
        if (sftp is not { } client || items.Count == 0)
            return;
        string what = items.Count == 1 ? items[0].Name : string.Format(CultureInfo.CurrentCulture, Strings.Sftp_DeleteMany, items.Count);
        if (confirm && !await ConfirmAsync(Strings.Action_Delete, string.Format(CultureInfo.CurrentCulture, Strings.Sftp_DeleteMessage, what), Strings.Action_Delete))
            return;
        await RunOperationAsync(() =>
        {
            foreach (SftpItem item in items)
                SftpTransfer.Delete(client, item.Entry);
        });
    }

    private async Task RunOperationAsync(Action operation)
    {
        if (busy)
            return;
        busy = true;
        try
        {
            await Task.Run(operation);
        }
        catch (Exception ex) when (IsSftpProblem(ex))
        {
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Sftp_OperationFailed, ex.Message));
        }
        finally
        {
            busy = false;
        }

        await NavigateAsync(CurrentPath);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool files = IsConnected && !busy && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropHintText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Sftp_DropHint, CurrentPath);
        DropHint.IsVisible = files;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropHint.IsVisible = false;
        string[] paths = [.. (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()];
        e.Handled = true;
        if (paths.Length > 0)
            _ = UploadAsync(paths);
    }

    private async Task<string?> AskNameAsync(string title, string current, string action)
    {
        var box = new TextBox { Text = current, MinWidth = 320 };
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = action,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        if (await dialog.ShowAsync(this) != FAContentDialogResult.Primary)
            return null;
        string name = box.Text?.Trim() ?? "";
        if (name.Length == 0 || name.Contains('/') || name is "." or "..")
        {
            SetStatus(Strings.Sftp_InvalidName);
            return null;
        }

        return name;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = action,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Close,
        };
        return await dialog.ShowAsync(this) == FAContentDialogResult.Primary;
    }

    private void Disconnect(string message)
    {
        transferCancel?.Cancel();
        SftpClient? client = sftp;
        SshRoute? r = route;
        sftp = null;
        route = null;
        if (r is null)
            return;
        try
        {
            client?.Disconnect();
        }
        catch (Exception ex) when (IsSftpProblem(ex))
        {
        }

        r.Dispose();
        SetStatus(message);
        SetStatusDot(Color.FromRgb(0x9C, 0xA3, 0xAF));
        DisconnectButton.IsEnabled = false;
        ReconnectButton.IsVisible = true;
        Browser.IsEnabled = false;
        Browser.IsVisible = false;
        Card.IsVisible = true;
    }

    private void ShowCard()
    {
        Browser.IsVisible = false;
        Card.IsVisible = true;
        Card.FocusInput();
    }

    private void UpdateButtons()
    {
        int count = Selected.Count;
        bool idle = IsConnected && !busy;
        DownloadButton.IsEnabled = DeleteButton.IsEnabled = idle && count > 0;
        RenameButton.IsEnabled = idle && count == 1;
        UploadButton.IsEnabled = NewFolderButton.IsEnabled = idle;
        Browser.IsEnabled = IsConnected;
    }

    private static bool IsSftpProblem(Exception ex) =>
        ex is SshException or IOException or InvalidOperationException or ObjectDisposedException or System.Net.Sockets.SocketException;

    private void SetStatusDot(Color color) => StatusDot.Fill = new SolidColorBrush(color);

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusLogged?.Invoke(this, message);
    }

    protected override void OnClosed(EventArgs e)
    {
        Disconnect("");
        base.OnClosed(e);
    }
}

/// <summary>A row of the SFTP list.</summary>
public sealed class SftpItem(SftpEntry entry)
{
    public SftpEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public FASymbol Symbol => Entry.IsDirectory ? FASymbol.FolderFilled : Entry.IsSymbolicLink ? FASymbol.Link : FASymbol.Document;
    public IBrush IconBrush => Entry.IsDirectory ? FolderBrush : FileBrush;
    public string SizeLabel => Entry.IsDirectory ? "" : FormatSize(Entry.Length);
    public string ModifiedLabel => Entry.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);

    private static readonly IBrush FolderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xB7, 0x3B));
    private static readonly IBrush FileBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", value, units[unit]);
    }
}

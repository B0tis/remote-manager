using System.Globalization;
using Avalonia.Controls;
using RemoteManager.App.Resources;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.RemoteTools;

namespace RemoteManager.App.Connections.RemoteTools;

/// <summary>
/// Start options of a TeamViewer, AnyDesk, or RustDesk connection: the options the tool really has,
/// the free argument field, and a live preview of the command line with the password masked.
/// </summary>
public partial class RemoteToolSettingsEditor : UserControl, ISettingsEditor
{
    private static readonly int[] Qualities = [0, 1, 2, 3, 4, 5];
    private static readonly int[] AccessModes = [-1, 0, 1, 2, 3, 9];

    private readonly ConnectionType type;
    private readonly IReadOnlyList<RemoteToolMode> modes;
    private readonly string toolPath;
    private RemoteToolSettings loaded = new();

    /// <summary>For the XAML previewer only.</summary>
    public RemoteToolSettingsEditor() : this(ConnectionType.TeamViewer)
    {
    }

    public RemoteToolSettingsEditor(ConnectionType type)
    {
        InitializeComponent();
        this.type = type;
        modes = RemoteToolCommandLine.ModesOf(type);
        RemoteToolInfo info = RemoteToolLocator.InfoFor(type);
        toolPath = Design.IsDesignMode ? info.ExeName : RemoteToolLauncher.Locator.Locate(type).Path ?? info.ExeName;

        ModeBox.ItemsSource = modes.Select(ModeLabel).ToList();
        QualityBox.ItemsSource = new[]
        {
            Strings.RemoteTool_Quality_None, Strings.RemoteTool_Quality_1, Strings.RemoteTool_Quality_2,
            Strings.RemoteTool_Quality_3, Strings.RemoteTool_Quality_4, Strings.RemoteTool_Quality_5,
        };
        AccessBox.ItemsSource = new[]
        {
            Strings.RemoteTool_Access_None, Strings.RemoteTool_Access_0, Strings.RemoteTool_Access_1,
            Strings.RemoteTool_Access_2, Strings.RemoteTool_Access_3, Strings.RemoteTool_Access_9,
        };

        TeamViewerRow.IsVisible = type == ConnectionType.TeamViewer;
        AnyDeskRow.IsVisible = type == ConnectionType.AnyDesk;
        PasswordOnLineBox.IsVisible = type != ConnectionType.AnyDesk;
        PasswordOnLineBox.Content = type == ConnectionType.TeamViewer ? Strings.RemoteTool_PasswordOnLine_TeamViewer : Strings.RemoteTool_PasswordOnLine_RustDesk;
        PasswordWarning.Message = string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_PasswordWarning_Message, info.ProductName);
        PasswordInfo.Text = type == ConnectionType.AnyDesk ? Strings.RemoteTool_PasswordInfo_AnyDesk : Strings.RemoteTool_PasswordInfo_Clipboard;

        ModeBox.SelectionChanged += (_, _) => UpdatePreview();
        QualityBox.SelectionChanged += (_, _) => UpdatePreview();
        AccessBox.SelectionChanged += (_, _) => UpdatePreview();
        FullScreenBox.IsCheckedChanged += (_, _) => UpdatePreview();
        PlainBox.IsCheckedChanged += (_, _) => UpdatePreview();
        PasswordOnLineBox.IsCheckedChanged += (_, _) => UpdatePreview();
        ArgumentsBox.TextChanged += (_, _) => UpdatePreview();
    }

    public Control View => this;

    /// <summary>The previewed command line (self-test).</summary>
    public string PreviewCommand => PreviewText.Text ?? "";

    /// <summary>The previewed arguments without the executable path (self-test; the path may contain anything).</summary>
    public IReadOnlyList<string> PreviewArguments { get; private set; } = [];
    public bool IsPasswordWarningShown => PasswordWarning.IsOpen;
    public CheckBox CommandLineCredentialBox => PasswordOnLineBox;
    public TextBox Arguments => ArgumentsBox;

    public void Load(ConnectionSettings settings)
    {
        loaded = (RemoteToolSettings)settings;
        AliasBox.Text = loaded.Alias;
        ModeBox.SelectedIndex = Math.Max(0, modes.ToList().IndexOf(loaded.Mode));
        QualityBox.SelectedIndex = Math.Max(0, Array.IndexOf(Qualities, loaded.Quality));
        AccessBox.SelectedIndex = Math.Max(0, Array.IndexOf(AccessModes, loaded.AccessControl));
        FullScreenBox.IsChecked = loaded.FullScreen;
        PlainBox.IsChecked = loaded.Plain;
        PasswordOnLineBox.IsChecked = loaded.CredentialOnCommandLine;
        ArgumentsBox.Text = loaded.ExtraArguments;
        UpdatePreview();
    }

    public string? Validate() =>
        RemoteToolCommandLine.FindSecretArgument(RemoteToolCommandLine.SplitArguments(ArgumentsBox.Text)) is { } secret
            ? string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Error_SecretInArguments, secret)
            : null;

    public ConnectionSettings Commit() => Current();

    private RemoteToolSettings Current() => loaded with
    {
        Alias = AliasBox.Text?.Trim() ?? "",
        Mode = modes[Math.Clamp(ModeBox.SelectedIndex, 0, modes.Count - 1)],
        Quality = Qualities[Math.Max(0, QualityBox.SelectedIndex)],
        AccessControl = AccessModes[Math.Max(0, AccessBox.SelectedIndex)],
        FullScreen = FullScreenBox.IsChecked == true,
        Plain = PlainBox.IsChecked == true,
        CredentialOnCommandLine = type != ConnectionType.AnyDesk && PasswordOnLineBox.IsChecked == true,
        ExtraArguments = ArgumentsBox.Text?.Trim() ?? "",
    };

    private void UpdatePreview()
    {
        RemoteToolSettings settings = Current();
        PasswordWarning.IsOpen = settings.CredentialOnCommandLine;
        // The preview assumes a stored password, so the reader sees where it would go (masked).
        RemoteToolCommand command = RemoteToolCommandLine.Build(type, toolPath, Strings.RemoteTool_PreviewId, settings, hasPassword: true);
        PreviewArguments = command.Arguments;
        PreviewText.Text = command.SecretViaStdin
            ? string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Preview_Stdin, command.Preview, RemoteToolCommand.Mask)
            : command.Preview;
    }

    private static string ModeLabel(RemoteToolMode mode) => mode switch
    {
        RemoteToolMode.FileTransfer => Strings.RemoteTool_Mode_FileTransfer,
        RemoteToolMode.Vpn => Strings.RemoteTool_Mode_Vpn,
        RemoteToolMode.PortForward => Strings.RemoteTool_Mode_PortForward,
        _ => Strings.RemoteTool_Mode_Control,
    };
}

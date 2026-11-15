using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Model;
using RemoteManager.App.Connections.Ssh;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Connections.Rdp;

/// <summary>
/// The RDP part of the connection editor: display, keyboard, security (NLA, server authentication,
/// admin session, Restricted Admin / Remote Credential Guard), gateway, redirections, RemoteApp and
/// start program, and "open with mstsc". A typed gateway password goes to the Credential Manager on
/// save; the settings only keep the reference. Each topic can be closed; its header then says what is set.
/// </summary>
public partial class RdpSettingsEditor : UserControl, ISettingsEditor
{
    private const int MinSize = 200, MaxSize = 8192;

    private static readonly RdpDisplayMode[] DisplayModes = [RdpDisplayMode.FitToWindow, RdpDisplayMode.SmartSizing, RdpDisplayMode.FixedResolution];
    private static readonly RdpKeyboardMode[] KeyboardModes = [RdpKeyboardMode.Session, RdpKeyboardMode.Local, RdpKeyboardMode.FullScreenOnly];
    private static readonly int[] AuthLevels = [2, 1, 0];
    private static readonly RdpGatewayUsage[] GatewayUsages = [RdpGatewayUsage.None, RdpGatewayUsage.Always, RdpGatewayUsage.Detect];
    private static readonly RdpAudioMode[] AudioModes = [RdpAudioMode.Local, RdpAudioMode.Remote, RdpAudioMode.None];

    private RdpSettings loaded = new();

    public RdpSettingsEditor()
    {
        InitializeComponent();
        DisplayBox.SelectionChanged += (_, _) => UpdateVisibility();
        GatewayUsageBox.SelectionChanged += (_, _) => UpdateVisibility();
        GatewaySameLogonBox.IsCheckedChanged += (_, _) => UpdateVisibility();
        RemoteAppBox.TextChanged += (_, _) => UpdateVisibility();

        // Any change may alter what a closed topic says about itself.
        foreach (ILogical input in this.GetLogicalDescendants())
        {
            switch (input)
            {
                case ComboBox box: box.SelectionChanged += (_, _) => UpdateSummaries(); break;
                case ToggleButton check: check.IsCheckedChanged += (_, _) => UpdateSummaries(); break;
                case TextBox text: text.TextChanged += (_, _) => UpdateSummaries(); break;
            }
        }

        FormFields.LinkLabels(this);
    }

    public Control View => this;

    public CheckBox LivePreview => LivePreviewBox;

    /// <summary>What the closed topics say in their headers, top to bottom (self-test).</summary>
    public IReadOnlyList<string> TopicSummaries =>
        [DisplaySummary.Text ?? "", SecuritySummary.Text ?? "", GatewaySummary.Text ?? "", RedirectionSummary.Text ?? "", ProgramSummary.Text ?? ""];

    /// <summary>Opens every section (self-test screenshots).</summary>
    public void ExpandAll()
    {
        foreach (Expander expander in new[] { DisplayExpander, SecurityExpander, GatewayExpander, RedirectionExpander, ProgramExpander })
            expander.IsExpanded = true;
    }

    public void Load(ConnectionSettings settings)
    {
        loaded = (RdpSettings)settings;
        RdpSettings s = loaded;
        DisplayBox.SelectedIndex = Math.Max(0, Array.IndexOf(DisplayModes, s.Display));
        WidthBox.Text = s.DesktopWidth > 0 ? s.DesktopWidth.ToString(CultureInfo.CurrentCulture) : "";
        HeightBox.Text = s.DesktopHeight > 0 ? s.DesktopHeight.ToString(CultureInfo.CurrentCulture) : "";
        KeyboardBox.SelectedIndex = Math.Max(0, Array.IndexOf(KeyboardModes, s.Keyboard));
        MultimonBox.IsChecked = s.UseMultimon;
        LivePreviewBox.IsChecked = s.LivePreview;
        ExternalBox.IsChecked = s.OpenExternally;

        NlaBox.IsChecked = s.EnableNla;
        AuthLevelBox.SelectedIndex = Math.Max(0, Array.IndexOf(AuthLevels, Math.Clamp(s.AuthenticationLevel, 0, 2)));
        AdminBox.IsChecked = s.AdminSession;
        CredentialModeBox.SelectedIndex = s.RestrictedAdmin ? 1 : s.RemoteCredentialGuard ? 2 : 0;

        GatewayUsageBox.SelectedIndex = Math.Max(0, Array.IndexOf(GatewayUsages, s.GatewayUsage));
        GatewayHostBox.Text = s.GatewayHost;
        GatewaySameLogonBox.IsChecked = s.GatewayUsesConnectionCredentials;
        GatewayUserBox.Text = s.GatewayUserName;
        GatewayPasswordBox.Text = "";
        GatewayPasswordBox.PlaceholderText = s.GatewayCredential is null ? "" : Strings.Password_NewPlaceholder;

        RdpRedirections r = s.Redirections;
        ClipboardBox.IsChecked = r.Clipboard;
        DrivesBox.IsChecked = r.Drives;
        PrintersBox.IsChecked = r.Printers;
        SmartCardsBox.IsChecked = r.SmartCards;
        PortsBox.IsChecked = r.Ports;
        MicrophoneBox.IsChecked = r.Microphone;
        AudioBox.SelectedIndex = Math.Max(0, Array.IndexOf(AudioModes, r.Audio));

        RemoteAppBox.Text = s.RemoteAppProgram;
        StartProgramBox.Text = s.StartProgram;
        WorkingDirectoryBox.Text = s.WorkingDirectory;
        JumpHostPicker.Fill(JumpBox, s.JumpHostId);

        // Show a section open when it holds something other than the defaults.
        SecurityExpander.IsExpanded = !s.EnableNla || s.AuthenticationLevel != 2 || s.AdminSession || s.RestrictedAdmin || s.RemoteCredentialGuard;
        GatewayExpander.IsExpanded = s.GatewayUsage != RdpGatewayUsage.None || s.JumpHostId is { Length: > 0 };
        RedirectionExpander.IsExpanded = r != new RdpRedirections();
        ProgramExpander.IsExpanded = s.RemoteAppProgram.Length > 0 || s.StartProgram.Length > 0;
        UpdateVisibility();
        UpdateSummaries();
    }

    public string? Validate()
    {
        if (SelectedDisplay == RdpDisplayMode.FixedResolution && (ParseSize(WidthBox.Text) is null || ParseSize(HeightBox.Text) is null))
        {
            DisplayExpander.IsExpanded = true;
            return string.Format(CultureInfo.CurrentCulture, Strings.Rdp_Error_Size, MinSize, MaxSize);
        }

        if (SelectedGateway != RdpGatewayUsage.None && string.IsNullOrWhiteSpace(GatewayHostBox.Text))
        {
            GatewayExpander.IsExpanded = true;
            return Strings.Rdp_Error_GatewayHost;
        }

        return null;
    }

    public ConnectionSettings Commit()
    {
        RdpDisplayMode display = SelectedDisplay;
        bool fixedSize = display == RdpDisplayMode.FixedResolution;
        RdpGatewayUsage gateway = SelectedGateway;
        bool ownGatewayLogon = gateway != RdpGatewayUsage.None && GatewaySameLogonBox.IsChecked != true;

        return loaded with
        {
            Display = display,
            DesktopWidth = fixedSize ? ParseSize(WidthBox.Text) ?? 0 : 0,
            DesktopHeight = fixedSize ? ParseSize(HeightBox.Text) ?? 0 : 0,
            Keyboard = KeyboardModes[Math.Max(0, KeyboardBox.SelectedIndex)],
            UseMultimon = MultimonBox.IsChecked == true,
            LivePreview = LivePreviewBox.IsChecked == true,
            OpenExternally = ExternalBox.IsChecked == true,
            EnableNla = NlaBox.IsChecked == true,
            AuthenticationLevel = AuthLevels[Math.Max(0, AuthLevelBox.SelectedIndex)],
            AdminSession = AdminBox.IsChecked == true,
            RestrictedAdmin = CredentialModeBox.SelectedIndex == 1,
            RemoteCredentialGuard = CredentialModeBox.SelectedIndex == 2,
            GatewayUsage = gateway,
            GatewayHost = GatewayHostBox.Text?.Trim() ?? "",
            GatewayUsesConnectionCredentials = !ownGatewayLogon,
            GatewayUserName = ownGatewayLogon ? GatewayUserBox.Text?.Trim() ?? "" : "",
            GatewayCredential = CommitGatewayPassword(ownGatewayLogon),
            Redirections = new RdpRedirections
            {
                Clipboard = ClipboardBox.IsChecked == true,
                Drives = DrivesBox.IsChecked == true,
                Printers = PrintersBox.IsChecked == true,
                SmartCards = SmartCardsBox.IsChecked == true,
                Ports = PortsBox.IsChecked == true,
                Microphone = MicrophoneBox.IsChecked == true,
                Audio = AudioModes[Math.Max(0, AudioBox.SelectedIndex)],
            },
            RemoteAppProgram = RemoteAppBox.Text?.Trim() ?? "",
            StartProgram = StartProgramBox.Text?.Trim() ?? "",
            WorkingDirectory = WorkingDirectoryBox.Text?.Trim() ?? "",
            JumpHostId = JumpHostPicker.SelectedId(JumpBox),
        };
    }

    private RdpDisplayMode SelectedDisplay => DisplayModes[Math.Max(0, DisplayBox.SelectedIndex)];
    private RdpGatewayUsage SelectedGateway => GatewayUsages[Math.Max(0, GatewayUsageBox.SelectedIndex)];

    /// <summary>
    /// Stores a typed gateway password (key "gateway-&lt;guid&gt;", kept across edits) or removes the stored
    /// one when the gateway no longer has its own logon. Returns the reference to keep.
    /// </summary>
    private CredentialRef? CommitGatewayPassword(bool ownLogon)
    {
        CredentialService credentials = AppHost.Current.Credentials;
        CredentialRef? current = loaded.GatewayCredential;
        string password = GatewayPasswordBox.Text ?? "";
        GatewayPasswordBox.Text = "";
        try
        {
            // The Credential Manager source completes synchronously.
            if (!ownLogon)
            {
                if (current is not null && credentials.SourceFor(current) is { IsWritable: true } source)
                    source.DeleteAsync(current.Key).AsTask().GetAwaiter().GetResult();
                return null;
            }

            if (password.Length == 0)
                return current;

            CredentialRef target = current is not null && credentials.SourceFor(current) is { IsWritable: true }
                ? current
                : new CredentialRef(credentials.DefaultSource.Id, $"gateway-{Guid.NewGuid():N}");
            credentials.SourceFor(target)!.WriteAsync(target.Key, password).AsTask().GetAwaiter().GetResult();
            return target;
        }
        catch (Win32Exception ex)
        {
            Trace.WriteLine($"Gateway password not stored: {ex.Message}");
            return current;
        }
    }

    private void UpdateVisibility()
    {
        SizeRow.IsVisible = SelectedDisplay == RdpDisplayMode.FixedResolution;
        GatewayFields.IsVisible = SelectedGateway != RdpGatewayUsage.None;
        GatewayLogonRow.IsVisible = GatewaySameLogonBox.IsChecked != true;
        // A RemoteApp always opens with mstsc.
        bool remoteApp = RemoteAppBox.Text?.Trim().Length > 0;
        ExternalBox.IsEnabled = !remoteApp;
        if (remoteApp)
            ExternalBox.IsChecked = true;
    }

    /// <summary>One line per topic for its header: enough to see the setup without opening anything.</summary>
    private void UpdateSummaries()
    {
        var display = new List<string>
        {
            SelectedDisplay switch
            {
                RdpDisplayMode.SmartSizing => Strings.Rdp_Summary_Smart,
                RdpDisplayMode.FixedResolution when ParseSize(WidthBox.Text) is { } width && ParseSize(HeightBox.Text) is { } height =>
                    string.Format(CultureInfo.CurrentCulture, Strings.Rdp_Summary_Size, width, height),
                RdpDisplayMode.FixedResolution => Strings.Rdp_Display_Fixed,
                _ => Strings.Rdp_Summary_Fit,
            },
        };
        if (MultimonBox.IsChecked == true)
            display.Add(Strings.Rdp_Summary_Multimon);
        if (ExternalBox.IsChecked == true)
            display.Add(Strings.Rdp_Summary_External);
        DisplaySummary.Text = string.Join(", ", display);

        var security = new List<string> { NlaBox.IsChecked == true ? Strings.Rdp_Summary_Nla : Strings.Rdp_Summary_NoNla };
        if (AdminBox.IsChecked == true)
            security.Add(Strings.Rdp_Summary_Admin);
        if (CredentialModeBox.SelectedIndex == 1)
            security.Add(Strings.Rdp_Summary_RestrictedAdmin);
        else if (CredentialModeBox.SelectedIndex == 2)
            security.Add(Strings.Rdp_CredentialMode_RemoteGuard);
        SecuritySummary.Text = string.Join(", ", security);

        string gatewayHost = GatewayHostBox.Text?.Trim() ?? "";
        var gateway = new List<string>
        {
            SelectedGateway switch
            {
                RdpGatewayUsage.None => Strings.Rdp_Summary_NoGateway,
                _ when gatewayHost.Length > 0 => gatewayHost,
                RdpGatewayUsage.Detect => Strings.Rdp_Summary_GatewayAuto,
                _ => Strings.Rdp_Gateway,
            },
        };
        if (JumpHostPicker.SelectedId(JumpBox) is { Length: > 0 })
            gateway.Add(Strings.Rdp_Summary_Tunnel);
        GatewaySummary.Text = string.Join(", ", gateway);

        List<string> redirected = [.. new[] { ClipboardBox, DrivesBox, PrintersBox, SmartCardsBox, PortsBox, MicrophoneBox }
            .Where(box => box.IsChecked == true).Select(box => box.Content as string ?? "")];
        if (redirected.Count == 0)
            redirected.Add(Strings.Rdp_Summary_NoRedirect);
        if (AudioModes[Math.Max(0, AudioBox.SelectedIndex)] is var audio && audio != RdpAudioMode.Local)
            redirected.Add(audio == RdpAudioMode.Remote ? Strings.Rdp_Summary_AudioRemote : Strings.Rdp_Summary_AudioNone);
        RedirectionSummary.Text = string.Join(", ", redirected);

        ProgramSummary.Text =
            RemoteAppBox.Text?.Trim() is { Length: > 0 } remoteApp ? remoteApp :
            StartProgramBox.Text?.Trim() is { Length: > 0 } program ? program :
            Strings.Rdp_Summary_Desktop;
    }

    private static int? ParseSize(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) && value is >= MinSize and <= MaxSize ? value : null;
}

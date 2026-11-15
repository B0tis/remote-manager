using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using RemoteManager.App.Resources;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Connections.Ssh;

/// <summary>Fills a ComboBox with "kein Jump-Host" plus every SSH connection (used by the SSH, SFTP, RDP, and VNC editors).</summary>
internal static class JumpHostPicker
{
    /// <summary>One entry: no jump host (no id), an SSH connection (name and host), or one that is gone (no host).</summary>
    private sealed record Choice(string? Id, string Name, string Host = "")
    {
        /// <summary>The text for type-ahead and for a screen reader.</summary>
        public override string ToString() => Host.Length > 0 ? $"{Name} ({Host})" : Name;
    }

    // The name as text, the host next to it the way hosts are written everywhere else: quieter, in the mono face.
    private static readonly FuncDataTemplate<Choice> Template = new((choice, _) =>
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = choice?.Name, VerticalAlignment = VerticalAlignment.Center });
        if (choice is { Host.Length: > 0 })
        {
            var host = new TextBlock { Text = choice.Host, VerticalAlignment = VerticalAlignment.Center };
            host.Classes.Add("mono");
            host.Classes.Add("muted");
            row.Children.Add(host);
        }

        return row;
    });

    public static void Fill(ComboBox box, string? selectedId)
    {
        var choices = new List<Choice> { new(null, Strings.Ssh_JumpHost_None) };
        choices.AddRange(AppHost.Current.Library.Connections
            .Where(c => c.Type == ConnectionType.Ssh)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new Choice(c.Id, c.Name, c.Host)));
        // A jump host that is gone (or not SSH any more) stays visible, so saving does not silently drop it.
        if (selectedId is { Length: > 0 } && choices.All(c => c.Id != selectedId))
            choices.Add(new Choice(selectedId, Strings.Ssh_JumpHost_Missing));

        box.ItemTemplate = Template;
        box.ItemsSource = choices;
        box.SelectedItem = choices.First(c => c.Id == (selectedId is { Length: > 0 } ? selectedId : null));
    }

    public static string? SelectedId(ComboBox box) => (box.SelectedItem as Choice)?.Id;
}

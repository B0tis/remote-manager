using Avalonia.Controls;
using RemoteManager.App.Resources;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Connections.Ssh;

/// <summary>Fills a ComboBox with "kein Jump-Host" plus every SSH connection (used by the SSH, SFTP, and RDP editors).</summary>
internal static class JumpHostPicker
{
    private sealed record Choice(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    public static void Fill(ComboBox box, string? selectedId)
    {
        var choices = new List<Choice> { new(null, Strings.Ssh_JumpHost_None) };
        choices.AddRange(AppHost.Current.Library.Connections
            .Where(c => c.Type == ConnectionType.Ssh)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new Choice(c.Id, $"{c.Name} ({c.Host})")));
        // A jump host that is gone (or not SSH any more) stays visible, so saving does not silently drop it.
        if (selectedId is { Length: > 0 } && choices.All(c => c.Id != selectedId))
            choices.Add(new Choice(selectedId, Strings.Ssh_JumpHost_Missing));

        box.ItemsSource = choices;
        box.SelectedItem = choices.First(c => c.Id == (selectedId is { Length: > 0 } ? selectedId : null));
    }

    public static string? SelectedId(ComboBox box) => (box.SelectedItem as Choice)?.Id;
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Views;

/// <summary>
/// Confirmation for deleting a group. An empty group only asks; a group with connections or subgroups says what it
/// holds and offers to move the content to the parent (default) or to delete it as well.
/// </summary>
public sealed class GroupDeleteDialog
{
    private readonly RadioButton? move;
    private readonly RadioButton? deleteAll;

    private GroupDeleteDialog(ConnectionLibrary library, ConnectionGroup group)
    {
        int connections = library.ConnectionsUnder(group.Id).Count();
        int subgroups = library.DescendantsOf(group.Id).Count();
        HasContent = connections + subgroups > 0;

        var content = new StackPanel { Spacing = 12, Width = 440 };
        if (!HasContent)
        {
            content.Children.Add(Text(Strings.GroupDelete_EmptyMessage));
        }
        else
        {
            string target = library.FindGroup(group.ParentId) is { } parent
                ? string.Format(CultureInfo.CurrentCulture, Strings.GroupDelete_MoveOption, library.PathOf(parent.Id))
                : Strings.GroupDelete_MoveToTopOption;
            move = new RadioButton { GroupName = "groupDelete", Content = target, IsChecked = true };
            deleteAll = new RadioButton { GroupName = "groupDelete", Content = Strings.GroupDelete_DeleteOption };
            // Starts where the option's text starts, so it reads as part of that option.
            Border warning = Note(Strings.GroupDelete_DeleteWarning, warning: true);
            warning.Margin = new Thickness(28, 4, 0, 0);
            warning.IsVisible = false;
            deleteAll.IsCheckedChanged += (_, _) => warning.IsVisible = deleteAll.IsChecked == true;

            content.Children.Add(Text(string.Format(CultureInfo.CurrentCulture, Strings.GroupDelete_Contains, Describe(connections, subgroups))));
            content.Children.Add(new StackPanel { Children = { move, deleteAll, warning } });
        }

        // Only said when there is one: the group's own password in the Credential Manager goes with the group.
        if (group.Credential?.Source == AppHost.Current.Credentials.DefaultSource.Id)
            content.Children.Add(Note(Strings.GroupDelete_PasswordNote, warning: false));

        Dialog = new FAContentDialog
        {
            Title = string.Format(CultureInfo.CurrentCulture, Strings.GroupDelete_Title, group.Name),
            Content = content,
            PrimaryButtonText = Strings.Action_Delete,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Close,
        };
    }

    /// <summary>The dialog while open (used by the self-test).</summary>
    public static GroupDeleteDialog? Current { get; private set; }

    public FAContentDialog Dialog { get; }

    /// <summary>True when the group holds connections or subgroups (the dialog then offers the two choices).</summary>
    public bool HasContent { get; }

    /// <summary>The selected choice; always <see cref="GroupRemoval.MoveContentUp"/> for an empty group.</summary>
    public GroupRemoval Choice
    {
        get => deleteAll?.IsChecked == true ? GroupRemoval.DeleteContent : GroupRemoval.MoveContentUp;
        set
        {
            if (deleteAll is null || move is null)
                return;
            deleteAll.IsChecked = value == GroupRemoval.DeleteContent;
            move.IsChecked = value == GroupRemoval.MoveContentUp;
        }
    }

    /// <summary>Asks how to delete <paramref name="group"/>; null when the user cancels.</summary>
    public static async Task<GroupRemoval?> AskAsync(ConnectionLibrary library, ConnectionGroup group)
    {
        var dialog = new GroupDeleteDialog(library, group);
        Current = dialog;
        try
        {
            return await dialog.Dialog.ShowAsync() == FAContentDialogResult.Primary ? dialog.Choice : null;
        }
        finally
        {
            Current = null;
        }
    }

    private static string Describe(int connections, int subgroups)
    {
        string Count(int n, string one, string many) => n == 1 ? one : string.Format(CultureInfo.CurrentCulture, many, n);
        var parts = new List<string>();
        if (connections > 0)
            parts.Add(Count(connections, Strings.GroupDelete_OneConnection, Strings.GroupDelete_Connections));
        if (subgroups > 0)
            parts.Add(Count(subgroups, Strings.GroupDelete_OneSubgroup, Strings.GroupDelete_Subgroups));
        return string.Join(Strings.GroupDelete_And, parts);
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    /// <summary>A remark in the form language of the editors (Border.note), with its sign in front.</summary>
    private static Border Note(string text, bool warning)
    {
        var icon = new FASymbolIcon
        {
            Symbol = warning ? FASymbol.ImportantFilled : FASymbol.Permissions,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        // Follows the theme like a DynamicResource in XAML.
        icon.Bind(TextElement.ForegroundProperty, icon.GetResourceObservable(warning ? "SystemFillColorCautionBrush" : "RmTextSecondaryBrush"));
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        Grid.SetColumn(message, 1);
        var note = new Border
        {
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Children = { icon, message } },
        };
        note.Classes.Add("note");
        if (warning)
            note.Classes.Add("warning");
        return note;
    }
}

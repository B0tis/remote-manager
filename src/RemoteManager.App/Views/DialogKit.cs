using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;

namespace RemoteManager.App.Views;

/// <summary>
/// What the small dialogs share, so that questions look and behave alike: one text width, the consequence as the
/// text, the verb on the primary button, and for a destructive question Cancel as the default button with the
/// verb drawn as a danger button (style class "danger", Theme/Styles.axaml).
/// </summary>
internal static class DialogKit
{
    /// <summary>Width of the text column of a question: long enough for two short lines, never a wall of text.</summary>
    public const double QuestionWidth = 440;

    /// <summary>Height of a content dialog around its content: title, button row, and paddings.</summary>
    private const double DialogChrome = 172;

    /// <summary>Tallest content dialog FluentAvalonia shows (ContentDialogMaxHeight).</summary>
    private const double DialogMaxHeight = 756;

    /// <summary>
    /// A question: <paramref name="title"/> names the action and its object, <paramref name="consequence"/> says
    /// what happens, <paramref name="verb"/> is the primary button. <paramref name="more"/> goes below the text
    /// (a note, a field).
    /// </summary>
    public static FAContentDialog Question(string title, string consequence, string verb, bool destructive = false, params Control[] more)
    {
        var content = new StackPanel { Spacing = 14, Width = QuestionWidth };
        content.Children.Add(Text(consequence));
        content.Children.AddRange(more);
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = verb,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Primary,
        };
        if (destructive)
            MakeDestructive(dialog);
        return dialog;
    }

    /// <summary>Body text of a dialog.</summary>
    public static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };

    /// <summary>A remark in a frame; <paramref name="kind"/> is null, "warning", or "error".</summary>
    public static Border Note(string text, string? kind = null) => Note(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 }, kind);

    public static Border Note(Control content, string? kind = null)
    {
        var note = new Border { Child = content };
        note.Classes.Add("note");
        if (kind is not null)
            note.Classes.Add(kind);
        return note;
    }

    /// <summary>A field of a dialog: its label above the input.</summary>
    public static StackPanel Field(string label, Control input, string? hint = null)
    {
        var name = new TextBlock { Text = label };
        name.Classes.Add("fieldLabel");
        var field = new StackPanel { Children = { name, input } };
        if (hint is not null)
        {
            var remark = new TextBlock { Text = hint, Margin = new Avalonia.Thickness(1, 6, 0, 0) };
            remark.Classes.Add("hint");
            field.Children.Add(remark);
        }

        return field;
    }

    /// <summary>
    /// Cancel becomes the default button (Enter and the first focus), and the verb is drawn as a danger button.
    /// FluentAvalonia paints the default button in the signal color; a question that destroys something has no
    /// recommended answer, so that mark is taken off again.
    /// </summary>
    public static void MakeDestructive(FAContentDialog dialog)
    {
        dialog.DefaultButton = FAContentDialogButton.Close;
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // The dialog styles its buttons while it opens; this runs right after.
            foreach (Button button in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.TemplatedParent == dialog))
            {
                if (button.Name == "CloseButton")
                    button.Classes.Remove("accent");
                else if (button.Name == "PrimaryButton")
                    button.Classes.Add("danger");
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Keeps a tall dialog content inside the window: its height follows the window, so a part of it can scroll
    /// while the rest (status, actions) stays in view. Without this the whole dialog scrolls and a scrolling part
    /// inside it gets a second scroll bar.
    /// </summary>
    public static void FitToWindow(Control content)
    {
        TopLevel? top = null;
        void Update(object? sender, SizeChangedEventArgs? e)
        {
            if (top is not null)
                content.MaxHeight = Math.Max(240, Math.Min(DialogMaxHeight, top.ClientSize.Height) - DialogChrome);
        }

        content.AttachedToVisualTree += (_, _) =>
        {
            top = TopLevel.GetTopLevel(content);
            if (top is null)
                return;
            top.SizeChanged += Update;
            Update(null, null);
        };
        content.DetachedFromVisualTree += (_, _) =>
        {
            if (top is not null)
                top.SizeChanged -= Update;
            top = null;
        };
    }

    /// <summary>Switches a state lamp (Ellipse.lamp): "ok" (green), "fault" (red), or "idle" (dark).</summary>
    public static void SetLamp(Ellipse lamp, string state)
    {
        foreach (string known in LampStates)
            lamp.Classes.Set(known, known == state);
    }

    private static readonly string[] LampStates = ["ok", "fault", "idle"];
}

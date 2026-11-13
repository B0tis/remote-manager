using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace RemoteManager.App.Views;

/// <summary>What a remark in a form says: a plain remark, a warning, or something about stored passwords.</summary>
internal enum NoteKind { Info, Secret, Warning }

/// <summary>
/// The shared parts of the form language (connection editor, group editor, import): the link between a field
/// label and its input, and the section heading and remark for forms that are built in code.
/// </summary>
internal static class FormFields
{
    /// <summary>
    /// Ties every field label of a form (a TextBlock with the class "fieldLabel") to the input that follows it,
    /// so a screen reader announces the field by its visible name. The self-test uses the same link to prove
    /// that no input is named by its placeholder alone.
    /// </summary>
    public static void LinkLabels(Control form)
    {
        foreach (TextBlock label in form.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("fieldLabel")))
        {
            // The input is the label's next sibling, or sits inside it (a row with a "Durchsuchen" button).
            if (label.Parent is Panel panel
                && panel.Children.SkipWhile(c => c != label).Skip(1).FirstOrDefault() is { } next
                && FirstInput(next) is { } input)
                AutomationProperties.SetLabeledBy(input, label);
        }
    }

    public static bool IsInput(Control control) => control is TextBox or ComboBox or AutoCompleteBox;

    /// <summary>A section heading with its hairline, as the XAML forms write it (Grid.sectionHead).</summary>
    public static Grid SectionHead(string text, bool first = false)
    {
        var heading = new TextBlock { Text = text };
        heading.Classes.Add("section");
        var rule = new Border();
        rule.Classes.Add("rule");
        Grid.SetColumn(rule, 1);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { heading, rule } };
        head.Classes.Add("sectionHead");
        if (first)
            head.Classes.Add("first");
        return head;
    }

    /// <summary>A remark (Border.note) with its sign in front.</summary>
    public static Border Note(string text, NoteKind kind = NoteKind.Info)
    {
        var icon = new FASymbolIcon
        {
            Symbol = kind switch
            {
                NoteKind.Warning => FASymbol.ImportantFilled,
                NoteKind.Secret => FASymbol.Permissions,
                _ => FASymbol.Help,
            },
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        // Follows the theme like a DynamicResource in XAML.
        icon.Bind(TextElement.ForegroundProperty, icon.GetResourceObservable(kind == NoteKind.Warning ? "SystemFillColorCautionBrush" : "RmTextSecondaryBrush"));
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        Grid.SetColumn(message, 1);
        var note = new Border
        {
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Children = { icon, message } },
        };
        note.Classes.Add("note");
        if (kind == NoteKind.Warning)
            note.Classes.Add("warning");
        return note;
    }

    private static Control? FirstInput(Control control) =>
        IsInput(control) ? control : control.GetLogicalDescendants().OfType<Control>().FirstOrDefault(IsInput);
}

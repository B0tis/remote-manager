using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace RemoteManager.App.Views;

/// <summary>
/// Ties every field label of a form (a TextBlock with the class "fieldLabel") to the input that follows it,
/// so a screen reader announces the field by its visible name. The self-test uses the same link to prove
/// that no input is named by its placeholder alone.
/// </summary>
internal static class FormFields
{
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

    private static Control? FirstInput(Control control) =>
        IsInput(control) ? control : control.GetLogicalDescendants().OfType<Control>().FirstOrDefault(IsInput);
}

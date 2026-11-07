using System.Windows;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace ApiWrangler.Desktop;

public static class EditorBinding
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string),
        typeof(EditorBinding), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached("Attached", typeof(bool),
        typeof(EditorBinding), new PropertyMetadata(false));
    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);

    public static readonly DependencyProperty SyntaxHighlightingProperty = DependencyProperty.RegisterAttached(
        "SyntaxHighlighting", typeof(string), typeof(EditorBinding), new PropertyMetadata(null, HighlightingChanged));
    public static string? GetSyntaxHighlighting(DependencyObject target) => (string?)target.GetValue(SyntaxHighlightingProperty);
    public static void SetSyntaxHighlighting(DependencyObject target, string? value) => target.SetValue(SyntaxHighlightingProperty, value);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextEditor editor) return;
        if (!(bool)editor.GetValue(AttachedProperty))
        {
            editor.SetValue(AttachedProperty, true);
            editor.TextChanged += (_, _) => editor.SetCurrentValue(TextProperty, editor.Text);
        }
        var text = args.NewValue as string ?? "";
        if (editor.Text != text) editor.Text = text;
    }

    private static void HighlightingChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextEditor editor) return;
        var name = args.NewValue as string;
        if (string.IsNullOrEmpty(name) || name.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            editor.SyntaxHighlighting = null;
        }
        else
        {
            editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition(name)
                ?? HighlightingManager.Instance.GetDefinitionByExtension("." + name.ToLowerInvariant())
                ?? (name.Equals("json", StringComparison.OrdinalIgnoreCase) ? HighlightingManager.Instance.GetDefinition("JavaScript") : null);
        }
    }
}

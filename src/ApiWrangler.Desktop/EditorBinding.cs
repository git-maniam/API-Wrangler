using System.Windows;
using ICSharpCode.AvalonEdit;

namespace ApiWrangler.Desktop;

public static class EditorBinding
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string),
        typeof(EditorBinding), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached("Attached", typeof(bool),
        typeof(EditorBinding), new PropertyMetadata(false));
    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);

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
}

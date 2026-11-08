using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace ApiWrangler.Desktop;

public static class DatasetHighlightingDefinitions
{
    private static bool registered;

    public static void RegisterAll()
    {
        if (registered) return;
        registered = true;

        try
        {
            RegisterXshd("CSV", [".csv", ".tsv"], CsvXshd);
            RegisterXshd("JSONL", [".jsonl"], JsonXshd);
            RegisterXshd("JSON", [".json"], JsonXshd);
        }
        catch
        {
            // Fallback to built-in highlighting if registration fails
        }
    }

    private static void RegisterXshd(string name, string[] extensions, string xshdXml)
    {
        using var reader = XmlReader.Create(new StringReader(xshdXml));
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        HighlightingManager.Instance.RegisterHighlighting(name, extensions, definition);
    }

    private const string CsvXshd = """
        <SyntaxDefinition name="CSV" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
            <Color name="Delimiter" foreground="#005FB8" fontWeight="bold" />
            <Color name="String" foreground="#A31515" />
            <Color name="Number" foreground="#098658" />
            
            <RuleSet>
                <Span color="String">
                    <Begin>"</Begin>
                    <End>"</End>
                    <RuleSet>
                        <Span begin="&quot;&quot;" end="" />
                    </RuleSet>
                </Span>
                <Rule color="Delimiter">
                    [,;\t]
                </Rule>
                <Rule color="Number">
                    \b\d+(\.\d+)?\b
                </Rule>
            </RuleSet>
        </SyntaxDefinition>
        """;

    private const string JsonXshd = """
        <SyntaxDefinition name="JSON" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
            <Color name="String" foreground="#A31515" />
            <Color name="Number" foreground="#098658" />
            <Color name="Keyword" foreground="#0000FF" fontWeight="bold" />
            <Color name="Punctuation" foreground="#005FB8" />
            
            <RuleSet>
                <Span color="String">
                    <Begin>"</Begin>
                    <End>"</End>
                    <RuleSet>
                        <Span begin="\\&quot;" end="" />
                    </RuleSet>
                </Span>
                <Keywords color="Keyword">
                    <Word>true</Word>
                    <Word>false</Word>
                    <Word>null</Word>
                </Keywords>
                <Rule color="Number">
                    -?\b\d+(\.\d+)?([eE][+-]?\d+)?\b
                </Rule>
                <Rule color="Punctuation">
                    [{}\[\]:,]
                </Rule>
            </RuleSet>
        </SyntaxDefinition>
        """;
}


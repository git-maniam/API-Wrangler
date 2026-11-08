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
            RegisterXshd("GraphQL", [".graphql", ".gql"], GraphQLXshd);
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

    private const string GraphQLXshd = """"
        <SyntaxDefinition name="GraphQL" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
            <Color name="Comment" foreground="#5C6370" />
            <Color name="String" foreground="#98C379" />
            <Color name="Number" foreground="#D19A66" />
            <Color name="Keyword" foreground="#E06C75" fontWeight="bold" />
            <Color name="Type" foreground="#E5C07B" />
            <Color name="Variable" foreground="#61AFEF" />
            <Color name="Directive" foreground="#C678DD" />
            <Color name="Punctuation" foreground="#ABB2BF" />

            <RuleSet>
                <Span color="Comment">
                    <Begin>#</Begin>
                </Span>

                <Span color="String">
                    <Begin>"""</Begin>
                    <End>"""</End>
                </Span>

                <Span color="String">
                    <Begin>"</Begin>
                    <End>"</End>
                    <RuleSet>
                        <Span begin="\\&quot;" end="" />
                    </RuleSet>
                </Span>

                <Keywords color="Keyword">
                    <Word>query</Word>
                    <Word>mutation</Word>
                    <Word>subscription</Word>
                    <Word>fragment</Word>
                    <Word>on</Word>
                    <Word>schema</Word>
                    <Word>type</Word>
                    <Word>interface</Word>
                    <Word>union</Word>
                    <Word>enum</Word>
                    <Word>scalar</Word>
                    <Word>input</Word>
                    <Word>extend</Word>
                    <Word>implements</Word>
                    <Word>true</Word>
                    <Word>false</Word>
                    <Word>null</Word>
                </Keywords>

                <Rule color="Variable">
                    \$[a-zA-Z0-9_]+
                </Rule>

                <Rule color="Directive">
                    @[a-zA-Z0-9_]+
                </Rule>

                <Rule color="Type">
                    \b(Int|Float|String|Boolean|ID)\b
                </Rule>

                <Rule color="Number">
                    -?\b\d+(\.\d+)?([eE][+-]?\d+)?\b
                </Rule>

                <Rule color="Punctuation">
                    [{}\[\]():,!=]
                </Rule>
            </RuleSet>
        </SyntaxDefinition>
        """";
}


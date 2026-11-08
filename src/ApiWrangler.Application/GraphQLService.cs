using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class GraphQLService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string GenerateIntrospectionQuery()
    {
        return """
        query IntrospectionQuery {
          __schema {
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            types {
              kind
              name
              description
              fields(includeDeprecated: true) {
                name
                description
                args {
                  name
                  description
                  type {
                    name
                    kind
                    ofType { name kind }
                  }
                  defaultValue
                }
                type {
                  name
                  kind
                  ofType { name kind }
                }
              }
            }
          }
        }
        """;
    }

    public static GraphQLSchemaDefinition ParseIntrospectionResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        JsonElement schemaElem;
        if (root.TryGetProperty("data", out var dataElem) && dataElem.TryGetProperty("__schema", out var sElem))
        {
            schemaElem = sElem;
        }
        else if (root.TryGetProperty("__schema", out var directSchema))
        {
            schemaElem = directSchema;
        }
        else
        {
            throw new FormatException("Introspection JSON must contain '__schema' element.");
        }

        string queryType = schemaElem.TryGetProperty("queryType", out var qt) && qt.TryGetProperty("name", out var qn)
            ? (qn.GetString() ?? "Query")
            : "Query";

        string? mutationType = schemaElem.TryGetProperty("mutationType", out var mt) && mt.TryGetProperty("name", out var mn)
            ? mn.GetString()
            : null;

        string? subscriptionType = schemaElem.TryGetProperty("subscriptionType", out var st) && st.TryGetProperty("name", out var sn)
            ? sn.GetString()
            : null;

        var types = new List<GraphQLTypeDefinition>();
        if (schemaElem.TryGetProperty("types", out var typesArray) && typesArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typesArray.EnumerateArray())
            {
                string name = t.TryGetProperty("name", out var tn) ? (tn.GetString() ?? "") : "";
                if (string.IsNullOrEmpty(name) || name.StartsWith("__")) continue; // Skip introspection internal types

                string kind = t.TryGetProperty("kind", out var tk) ? (tk.GetString() ?? "OBJECT") : "OBJECT";
                string? desc = t.TryGetProperty("description", out var td) ? td.GetString() : null;

                var fields = new List<GraphQLFieldDefinition>();
                if (t.TryGetProperty("fields", out var fieldsArray) && fieldsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fieldsArray.EnumerateArray())
                    {
                        string fName = f.TryGetProperty("name", out var fn) ? (fn.GetString() ?? "") : "";
                        string fType = ResolveTypeName(f.TryGetProperty("type", out var ft) ? ft : default);
                        string? fDesc = f.TryGetProperty("description", out var fd) ? fd.GetString() : null;

                        var args = new List<GraphQLArgumentDefinition>();
                        if (f.TryGetProperty("args", out var argsArray) && argsArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var a in argsArray.EnumerateArray())
                            {
                                string aName = a.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "";
                                string aType = ResolveTypeName(a.TryGetProperty("type", out var at) ? at : default);
                                string? aDef = a.TryGetProperty("defaultValue", out var ad) ? ad.GetString() : null;
                                args.Add(new GraphQLArgumentDefinition(aName, aType, aDef));
                            }
                        }

                        fields.Add(new GraphQLFieldDefinition(fName, fType, fDesc, args));
                    }
                }

                types.Add(new GraphQLTypeDefinition(name, kind, desc, fields));
            }
        }

        return new GraphQLSchemaDefinition(types, queryType, mutationType, subscriptionType);
    }

    private static string ResolveTypeName(JsonElement typeElem)
    {
        if (typeElem.ValueKind != JsonValueKind.Object) return "String";
        if (typeElem.TryGetProperty("name", out var n) && n.GetString() is { } name && !string.IsNullOrEmpty(name))
        {
            return name;
        }
        if (typeElem.TryGetProperty("ofType", out var ofType) && ofType.ValueKind == JsonValueKind.Object)
        {
            return ResolveTypeName(ofType);
        }
        return "String";
    }

    public static GraphQLSchemaDefinition ParseSdl(string sdl)
    {
        if (string.IsNullOrWhiteSpace(sdl))
        {
            throw new ArgumentException("SDL content cannot be empty.", nameof(sdl));
        }

        var types = new List<GraphQLTypeDefinition>();
        string queryType = "Query";
        string? mutationType = null;
        string? subscriptionType = null;

        // Strip comments (# ...)
        string clean = Regex.Replace(sdl, @"#[^\r\n]*", "");

        // Find schema { query: ..., mutation: ... }
        var schemaMatch = Regex.Match(clean, @"schema\s*\{([^}]+)\}", RegexOptions.IgnoreCase);
        if (schemaMatch.Success)
        {
            string schemaBody = schemaMatch.Groups[1].Value;
            var qm = Regex.Match(schemaBody, @"query\s*:\s*(\w+)", RegexOptions.IgnoreCase);
            if (qm.Success) queryType = qm.Groups[1].Value;

            var mm = Regex.Match(schemaBody, @"mutation\s*:\s*(\w+)", RegexOptions.IgnoreCase);
            if (mm.Success) mutationType = mm.Groups[1].Value;

            var sm = Regex.Match(schemaBody, @"subscription\s*:\s*(\w+)", RegexOptions.IgnoreCase);
            if (sm.Success) subscriptionType = sm.Groups[1].Value;
        }

        // Match types: type Name { fields } or input Name { fields }
        var typeMatches = Regex.Matches(clean, @"(type|input|interface)\s+(\w+)[^{]*\{([^}]+)\}", RegexOptions.IgnoreCase);
        foreach (Match match in typeMatches)
        {
            string kindStr = match.Groups[1].Value.ToUpperInvariant();
            string kind = kindStr == "INPUT" ? "INPUT_OBJECT" : "OBJECT";
            string name = match.Groups[2].Value;
            string fieldsBody = match.Groups[3].Value;

            if (name.Equals("Query", StringComparison.OrdinalIgnoreCase) && queryType == "Query") queryType = name;
            if (name.Equals("Mutation", StringComparison.OrdinalIgnoreCase) && mutationType == null) mutationType = name;
            if (name.Equals("Subscription", StringComparison.OrdinalIgnoreCase) && subscriptionType == null) subscriptionType = name;

            var fields = new List<GraphQLFieldDefinition>();
            // Matches fieldName(args): FieldType or fieldName: FieldType
            var fieldMatches = Regex.Matches(fieldsBody, @"(\w+)(?:\(([^)]+)\))?\s*:\s*([!\[\]\w]+)");
            foreach (Match fm in fieldMatches)
            {
                string fName = fm.Groups[1].Value;
                string argsRaw = fm.Groups[2].Success ? fm.Groups[2].Value : "";
                string fType = fm.Groups[3].Value;

                var args = new List<GraphQLArgumentDefinition>();
                if (!string.IsNullOrWhiteSpace(argsRaw))
                {
                    var argMatches = Regex.Matches(argsRaw, @"(\w+)\s*:\s*([!\[\]\w]+)(?:\s*=\s*([^,]+))?");
                    foreach (Match am in argMatches)
                    {
                        string aName = am.Groups[1].Value;
                        string aType = am.Groups[2].Value;
                        string? aDef = am.Groups[3].Success ? am.Groups[3].Value.Trim() : null;
                        bool required = aType.EndsWith('!');
                        args.Add(new GraphQLArgumentDefinition(aName, aType, aDef, required));
                    }
                }

                fields.Add(new GraphQLFieldDefinition(fName, fType, null, args));
            }

            types.Add(new GraphQLTypeDefinition(name, kind, null, fields));
        }

        return new GraphQLSchemaDefinition(types, queryType, mutationType, subscriptionType);
    }

    public static IReadOnlyList<string> GetCompletionSuggestions(
        GraphQLSchemaDefinition schema,
        string typeName,
        string prefix = "")
    {
        var targetType = schema.Types.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        if (targetType?.Fields is null) return Array.Empty<string>();

        return targetType.Fields
            .Where(f => string.IsNullOrEmpty(prefix) || f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name)
            .OrderBy(n => n)
            .ToList();
    }

    public static IReadOnlyList<string> ValidateQuery(GraphQLSchemaDefinition schema, string query)
    {
        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
        {
            diagnostics.Add("GraphQL query cannot be empty.");
            return diagnostics;
        }

        // Determine operation root type: query -> QueryType, mutation -> MutationType
        string clean = Regex.Replace(query, @"#[^\r\n]*", "");
        bool isMutation = Regex.IsMatch(clean, @"^\s*mutation\b", RegexOptions.IgnoreCase);
        string rootTypeName = isMutation ? (schema.MutationType ?? "Mutation") : schema.QueryType;

        var rootType = schema.Types.FirstOrDefault(t => t.Name.Equals(rootTypeName, StringComparison.OrdinalIgnoreCase));
        if (rootType == null)
        {
            diagnostics.Add($"Schema does not define root operation type '{rootTypeName}'.");
            return diagnostics;
        }

        int firstBrace = clean.IndexOf('{');
        if (firstBrace < 0)
        {
            diagnostics.Add("GraphQL query contains no selection set.");
            return diagnostics;
        }

        // Extract depth-1 fields
        var topLevelFields = new List<string>();
        int depth = 0;
        var currentToken = new StringBuilder();
        bool inArgs = false;
        bool inQuotes = false;

        for (int i = firstBrace; i < clean.Length; i++)
        {
            char c = clean[i];

            if (c == '"' && (i == 0 || clean[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (inQuotes) continue;

            if (c == '(')
            {
                if (currentToken.Length > 0)
                {
                    topLevelFields.Add(currentToken.ToString());
                    currentToken.Clear();
                }
                inArgs = true;
                continue;
            }
            if (c == ')')
            {
                inArgs = false;
                continue;
            }
            if (inArgs) continue;

            if (c == '{')
            {
                if (currentToken.Length > 0)
                {
                    topLevelFields.Add(currentToken.ToString());
                    currentToken.Clear();
                }
                depth++;
                continue;
            }
            if (c == '}')
            {
                if (currentToken.Length > 0)
                {
                    topLevelFields.Add(currentToken.ToString());
                    currentToken.Clear();
                }
                depth--;
                if (depth == 0) break;
                continue;
            }

            if (depth == 1)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    currentToken.Append(c);
                }
                else
                {
                    if (currentToken.Length > 0)
                    {
                        topLevelFields.Add(currentToken.ToString());
                        currentToken.Clear();
                    }
                }
            }
        }

        if (currentToken.Length > 0)
        {
            topLevelFields.Add(currentToken.ToString());
        }

        foreach (var fieldName in topLevelFields)
        {
            if (fieldName.StartsWith("__") || fieldName is "query" or "mutation" or "subscription") continue;
            var matchedField = rootType.Fields?.FirstOrDefault(f => f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            if (matchedField == null)
            {
                diagnostics.Add($"Field '{fieldName}' does not exist on type '{rootTypeName}'.");
            }
        }

        return diagnostics;
    }

    public static async Task<GraphQLExecutionResult> ExecuteAsync(
        HttpClient client,
        string url,
        GraphQLRequestDefinition request,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken token = default)
    {
        var payload = new JsonObject
        {
            ["query"] = request.Query
        };
        if (!string.IsNullOrEmpty(request.OperationName))
        {
            payload["operationName"] = request.OperationName;
        }
        if (request.Variables is not null)
        {
            payload["variables"] = request.Variables.DeepClone();
        }

        string jsonPayload = payload.ToJsonString();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                httpRequest.Headers.TryAddWithoutValidation(k, v);
            }
        }

        var sw = Stopwatch.StartNew();
        using HttpResponseMessage response = await client.SendAsync(httpRequest, token).ConfigureAwait(false);
        string responseText = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        sw.Stop();

        JsonElement? data = null;
        var errors = new List<GraphQLErrorEntry>();

        if (!string.IsNullOrWhiteSpace(responseText))
        {
            try
            {
                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;

                if (root.TryGetProperty("data", out var dElem) && dElem.ValueKind != JsonValueKind.Null)
                {
                    data = dElem.Clone();
                }

                if (root.TryGetProperty("errors", out var errArray) && errArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var err in errArray.EnumerateArray())
                    {
                        string msg = err.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "Unknown error";
                        var pathList = new List<string>();
                        if (err.TryGetProperty("path", out var pArray) && pArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var p in pArray.EnumerateArray()) pathList.Add(p.ToString());
                        }

                        var locList = new List<GraphQLSourceLocation>();
                        if (err.TryGetProperty("locations", out var lArray) && lArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var l in lArray.EnumerateArray())
                            {
                                int line = l.TryGetProperty("line", out var ln) ? ln.GetInt32() : 0;
                                int col = l.TryGetProperty("column", out var cl) ? cl.GetInt32() : 0;
                                locList.Add(new GraphQLSourceLocation(line, col));
                            }
                        }

                        string? code = null;
                        Dictionary<string, JsonElement>? extDict = null;
                        if (err.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object)
                        {
                            extDict = new Dictionary<string, JsonElement>();
                            foreach (var prop in ext.EnumerateObject())
                            {
                                extDict[prop.Name] = prop.Value.Clone();
                            }
                            if (ext.TryGetProperty("code", out var cVal))
                            {
                                code = cVal.GetString();
                            }
                        }

                        errors.Add(new GraphQLErrorEntry(msg, pathList, locList, code, extDict));
                    }
                }
            }
            catch
            {
                // Fall back if response is not JSON
                errors.Add(new GraphQLErrorEntry(responseText, Array.Empty<string>(), Array.Empty<GraphQLSourceLocation>()));
            }
        }

        return new GraphQLExecutionResult(
            StatusCode: (int)response.StatusCode,
            Data: data,
            Errors: errors,
            RawJson: responseText,
            Duration: sw.Elapsed
        );
    }

    public static GraphQLFormatResult FormatDocument(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return new GraphQLFormatResult(true, document, Array.Empty<string>());
        }

        try
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            bool inComment = false;
            bool isEscaped = false;
            char stringChar = '"';
            var lines = document.Replace("\r\n", "\n").Split('\n');
            var errors = new List<string>();

            // Clean, format, and indent tokens
            for (int l = 0; l < lines.Length; l++)
            {
                string line = lines[l].Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Handle comments
                if (line.StartsWith('#'))
                {
                    sb.Append(new string(' ', indent * 2));
                    sb.AppendLine(line);
                    continue;
                }

                var lineBuilder = new StringBuilder();
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];

                    if (inComment)
                    {
                        lineBuilder.Append(c);
                        continue;
                    }

                    if (inString)
                    {
                        lineBuilder.Append(c);
                        if (c == '\\' && !isEscaped)
                        {
                            isEscaped = true;
                        }
                        else
                        {
                            if (c == stringChar && !isEscaped)
                            {
                                inString = false;
                            }
                            isEscaped = false;
                        }
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = true;
                        stringChar = '"';
                        lineBuilder.Append(c);
                        continue;
                    }

                    if (c == '#')
                    {
                        inComment = true;
                        lineBuilder.Append(c);
                        continue;
                    }

                    if (c == '{')
                    {
                        if (lineBuilder.Length > 0 && lineBuilder[^1] != ' ')
                        {
                            lineBuilder.Append(' ');
                        }
                        lineBuilder.Append("{\n");
                        indent++;
                        sb.Append(new string(' ', Math.Max(0, indent - 1) * 2));
                        sb.Append(lineBuilder.ToString());
                        lineBuilder.Clear();
                        sb.Append(new string(' ', indent * 2));
                        continue;
                    }

                    if (c == '}')
                    {
                        if (lineBuilder.Length > 0 && !string.IsNullOrWhiteSpace(lineBuilder.ToString()))
                        {
                            sb.Append(new string(' ', indent * 2));
                            sb.AppendLine(lineBuilder.ToString().Trim());
                            lineBuilder.Clear();
                        }
                        indent = Math.Max(0, indent - 1);
                        sb.Append(new string(' ', indent * 2));
                        sb.AppendLine("}");
                        continue;
                    }

                    lineBuilder.Append(c);
                }

                inComment = false;
                string remaining = lineBuilder.ToString().Trim();
                if (!string.IsNullOrEmpty(remaining))
                {
                    sb.Append(new string(' ', indent * 2));
                    sb.AppendLine(remaining);
                }
            }

            if (inString)
            {
                errors.Add("Unterminated string literal in GraphQL document.");
            }

            if (indent != 0)
            {
                errors.Add($"Mismatched braces: nesting level ended at {indent}.");
            }

            string formatted = sb.ToString().TrimEnd() + Environment.NewLine;
            return new GraphQLFormatResult(errors.Count == 0, errors.Count == 0 ? formatted : document, errors);
        }
        catch (Exception ex)
        {
            return new GraphQLFormatResult(false, document, [$"Formatting failed: {ex.Message}"]);
        }
    }
}

public sealed record GraphQLFormatResult(
    bool Success,
    string FormattedText,
    IReadOnlyList<string> Diagnostics
);

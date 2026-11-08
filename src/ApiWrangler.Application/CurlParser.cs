using System.Text;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public static class CurlParser
{
    public static HttpRequestDefinition Parse(string curlCommand)
    {
        if (string.IsNullOrWhiteSpace(curlCommand))
        {
            throw new ArgumentException("cURL command cannot be empty.", nameof(curlCommand));
        }

        var tokens = Tokenize(curlCommand);
        if (tokens.Count == 0)
        {
            throw new ArgumentException("cURL command contains no tokens.", nameof(curlCommand));
        }

        // Drop leading "curl" or "curl.exe" if present
        int index = 0;
        if (tokens[index].Equals("curl", StringComparison.OrdinalIgnoreCase) ||
            tokens[index].Equals("curl.exe", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        string? method = null;
        string? url = null;
        var headers = new List<HeaderEntry>();
        var bodyParts = new List<string>();
        var formData = new List<FormDataEntry>();
        string? basicAuth = null;
        bool followRedirects = false;
        bool ignoreTls = false;

        while (index < tokens.Count)
        {
            string token = tokens[index];

            if (token is "-X" or "--request")
            {
                index++;
                if (index < tokens.Count) method = tokens[index].ToUpperInvariant();
            }
            else if (token is "-H" or "--header")
            {
                index++;
                if (index < tokens.Count)
                {
                    string headerRaw = tokens[index];
                    int colon = headerRaw.IndexOf(':');
                    if (colon > 0)
                    {
                        string name = headerRaw[..colon].Trim();
                        string value = headerRaw[(colon + 1)..].Trim();
                        headers.Add(new HeaderEntry(name, value));
                    }
                }
            }
            else if (token is "-d" or "--data" or "--data-raw" or "--data-ascii" or "--data-binary")
            {
                index++;
                if (index < tokens.Count) bodyParts.Add(tokens[index]);
            }
            else if (token is "--data-urlencode")
            {
                index++;
                if (index < tokens.Count) bodyParts.Add(tokens[index]);
            }
            else if (token is "-u" or "--user")
            {
                index++;
                if (index < tokens.Count) basicAuth = tokens[index];
            }
            else if (token is "-A" or "--user-agent")
            {
                index++;
                if (index < tokens.Count) headers.Add(new HeaderEntry("User-Agent", tokens[index]));
            }
            else if (token is "-b" or "--cookie")
            {
                index++;
                if (index < tokens.Count) headers.Add(new HeaderEntry("Cookie", tokens[index]));
            }
            else if (token is "-e" or "--referer")
            {
                index++;
                if (index < tokens.Count) headers.Add(new HeaderEntry("Referer", tokens[index]));
            }
            else if (token is "-F" or "--form")
            {
                index++;
                if (index < tokens.Count)
                {
                    string formRaw = tokens[index];
                    int eq = formRaw.IndexOf('=');
                    if (eq > 0)
                    {
                        string name = formRaw[..eq].Trim();
                        string value = formRaw[(eq + 1)..].Trim();
                        formData.Add(new FormDataEntry(name, value));
                    }
                    else
                    {
                        formData.Add(new FormDataEntry(formRaw, ""));
                    }
                }
            }
            else if (token is "-L" or "--location")
            {
                followRedirects = true;
            }
            else if (token is "-k" or "--insecure")
            {
                ignoreTls = true;
            }
            else if (token == "--url")
            {
                index++;
                if (index < tokens.Count) url = tokens[index];
            }
            else if (!token.StartsWith('-'))
            {
                if (url == null)
                {
                    url = token;
                }
            }

            index++;
        }

        if (string.IsNullOrEmpty(url))
        {
            throw new FormatException("cURL command did not contain a target URL.");
        }

        // Clean up quoted URLs or trailing whitespace
        url = url.Trim('\'', '"');

        // Determine method default
        if (string.IsNullOrEmpty(method))
        {
            method = (bodyParts.Count > 0 || formData.Count > 0) ? "POST" : "GET";
        }

        // Extract query parameters from URL if present
        var queryParams = new List<QueryParamEntry>();
        int queryIndex = url.IndexOf('?');
        if (queryIndex >= 0 && queryIndex < url.Length - 1)
        {
            string queryString = url[(queryIndex + 1)..];
            string[] pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                int eq = pair.IndexOf('=');
                if (eq >= 0)
                {
                    string qName = Uri.UnescapeDataString(pair[..eq]);
                    string qVal = Uri.UnescapeDataString(pair[(eq + 1)..]);
                    queryParams.Add(new QueryParamEntry(qName, qVal));
                }
                else
                {
                    queryParams.Add(new QueryParamEntry(Uri.UnescapeDataString(pair), ""));
                }
            }
        }

        // Handle Body
        RequestBodyDefinition? bodyDef = null;
        if (formData.Count > 0)
        {
            bodyDef = new RequestBodyDefinition
            {
                Mode = "formdata",
                FormData = formData
            };
        }
        else if (bodyParts.Count > 0)
        {
            bodyDef = new RequestBodyDefinition
            {
                Mode = "raw",
                Text = string.Join("&", bodyParts)
            };
        }

        // Handle Auth
        var authDef = new RequestAuthDefinition();
        if (!string.IsNullOrEmpty(basicAuth))
        {
            int colon = basicAuth.IndexOf(':');
            if (colon >= 0)
            {
                authDef = new RequestAuthDefinition
                {
                    Type = "basic",
                    Username = basicAuth[..colon],
                    Password = basicAuth[(colon + 1)..]
                };
            }
            else
            {
                authDef = new RequestAuthDefinition
                {
                    Type = "basic",
                    Username = basicAuth,
                    Password = ""
                };
            }
        }

        string requestName = "cURL Request";
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri))
            {
                string path = parsedUri.AbsolutePath.Trim('/');
                requestName = string.IsNullOrEmpty(path) ? parsedUri.Host : $"{method} {path}";
            }
        }
        catch
        {
            // fallback
        }

        return new HttpRequestDefinition
        {
            Id = Guid.NewGuid(),
            Name = requestName,
            Method = method,
            Url = url,
            Headers = headers,
            Query = queryParams,
            BodyDefinition = bodyDef,
            Auth = authDef,
            TransportOptions = new HttpTransportOptions
            {
                FollowRedirects = followRedirects,
                DangerousIgnoreTlsErrors = ignoreTls
            }
        };
    }

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool escapeNext = false;

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];

            if (escapeNext)
            {
                sb.Append(c);
                escapeNext = false;
                continue;
            }

            if (c == '\\' && !inSingleQuote)
            {
                if (i + 1 < command.Length && command[i + 1] is '\r' or '\n')
                {
                    // Line continuation: \ followed by newline
                    if (command[i + 1] == '\r' && i + 2 < command.Length && command[i + 2] == '\n') i++;
                    i++;
                    continue;
                }
                escapeNext = true;
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inSingleQuote && !inDoubleQuote)
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
        }

        return tokens;
    }
}

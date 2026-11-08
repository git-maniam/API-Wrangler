using System.Runtime.CompilerServices;
using System.Text;

namespace ApiWrangler.Application;

/// <summary>Reads raw dataset values without implicit numeric, date, or null conversion.</summary>
public static class DelimitedDatasetReader
{
    public static async IAsyncEnumerable<IReadOnlyDictionary<string, string>> ReadAsync(
        TextReader reader, char delimiter = ',', int maxRecordCharacters = 1_048_576,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (delimiter is '"' or '\r' or '\n' or '\0' or '\uFEFF')
        {
            throw new ArgumentException("Invalid field delimiter.", nameof(delimiter));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecordCharacters);

        string[]? headers = null;
        var field = new StringBuilder();
        var fields = new List<string>();
        var buffer = new char[4096];
        var quoted = false;
        var afterQuote = false;
        var skipLf = false;
        var firstCharacter = true;
        var recordCharacters = 0;
        var recordNumber = 1;

        IReadOnlyDictionary<string, string>? CompleteRecord()
        {
            fields.Add(field.ToString());
            field.Clear();
            IReadOnlyDictionary<string, string>? row = null;
            if (headers is null)
            {
                headers = fields.ToArray();
                if (headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct(StringComparer.Ordinal).Count() != headers.Length)
                {
                    throw new FormatException("Dataset headers must be nonempty and unique.");
                }
            }
            else
            {
                if (fields.Count != headers.Length)
                {
                    throw new FormatException($"Record {recordNumber} has {fields.Count} fields; expected {headers.Length}.");
                }
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < headers.Length; i++)
                {
                    values.Add(headers[i], fields[i]);
                }
                row = values;
            }
            fields.Clear();
            recordCharacters = 0;
            recordNumber++;
            afterQuote = false;
            return row;
        }

        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var character = buffer[i];
                if (firstCharacter)
                {
                    firstCharacter = false;
                    if (character == '\uFEFF') { continue; }
                }
                if (skipLf)
                {
                    skipLf = false;
                    if (character == '\n') { continue; }
                }
                if (++recordCharacters > maxRecordCharacters)
                {
                    throw new FormatException($"Record {recordNumber} exceeds the configured character limit.");
                }
                if (quoted)
                {
                    if (character == '"') { quoted = false; afterQuote = true; }
                    else { field.Append(character); }
                    continue;
                }
                if (afterQuote && character == '"')
                {
                    field.Append('"');
                    quoted = true;
                    afterQuote = false;
                    continue;
                }
                if (character == delimiter)
                {
                    fields.Add(field.ToString());
                    field.Clear();
                    afterQuote = false;
                }
                else if (character is '\r' or '\n')
                {
                    skipLf = character == '\r';
                    var row = CompleteRecord();
                    if (row is not null) { yield return row; }
                }
                else if (character == '"' && field.Length == 0 && !afterQuote)
                {
                    quoted = true;
                }
                else
                {
                    if (afterQuote || character == '"')
                    {
                        throw new FormatException($"Invalid quoting in record {recordNumber}.");
                    }
                    field.Append(character);
                }
            }
        }
        if (quoted) { throw new FormatException($"Unterminated quoted field in record {recordNumber}."); }
        if (recordCharacters > 0)
        {
            var row = CompleteRecord();
            if (row is not null) { yield return row; }
        }
    }
}

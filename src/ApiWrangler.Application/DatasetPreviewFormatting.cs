namespace ApiWrangler.Application;

public static class DatasetPreviewFormatting
{
    public static string DetectHighlighting(string format) => format?.ToUpperInvariant() switch
    {
        "JSON" => "JSON",
        "JSONL" => "JSONL",
        _ => "CSV"
    };
}

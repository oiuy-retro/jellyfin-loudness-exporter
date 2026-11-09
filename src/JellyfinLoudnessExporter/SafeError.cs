namespace JellyfinLoudnessExporter;

internal static class SafeError
{
    internal enum Category
    {
        Usage,
        Io,
        Json,
        Mapping,
        Http,
        Internal
    }

    public static string Format(Category category, string code)
    {
        // Never include paths, URLs, tokens, or root absolutes.
        return category switch
        {
            Category.Usage => $"usage_error:{code}",
            Category.Io => $"io_error:{code}",
            Category.Json => $"json_error:{code}",
            Category.Mapping => $"mapping_error:{code}",
            Category.Http => $"http_error:{code}",
            _ => $"internal_error:{code}"
        };
    }
}

internal sealed class SafeErrorException : Exception
{
    public SafeError.Category Category { get; }
    public string Code { get; }

    public SafeErrorException(SafeError.Category category, string code)
        : base(SafeError.Format(category, code))
    {
        Category = category;
        Code = code;
    }
}

namespace UsenetSharp.Models;

public record UsenetArticleHeader
{
    public required Dictionary<string, string> Headers { get; init; }

    // Common header accessors for convenience
    public string? Subject => Headers.GetValueOrDefault("Subject");
    public string? From => Headers.GetValueOrDefault("From");
    public string? Date => Headers.GetValueOrDefault("Date");
    public string? MessageId => Headers.GetValueOrDefault("Message-ID");
    public string? References => Headers.GetValueOrDefault("References");
    public string? ContentType => Headers.GetValueOrDefault("Content-Type");
    public string? ContentTransferEncoding => Headers.GetValueOrDefault("Content-Transfer-Encoding");
    public string? Newsgroups => Headers.GetValueOrDefault("Newsgroups");
    public string? XrefFull => Headers.GetValueOrDefault("Xref");
    public string? Lines => Headers.GetValueOrDefault("Lines");
    public string? Bytes => Headers.GetValueOrDefault("Bytes");
}
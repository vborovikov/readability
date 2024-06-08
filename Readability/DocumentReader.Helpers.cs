namespace Readability;

using System;
using System.Diagnostics.CodeAnalysis;
using Brackets;

public partial class DocumentReader
{
    public static bool TryMakeAbsoluteUrl(string documentUrl, string url, [MaybeNullWhen(false)] out string absoluteUrl)
    {
        return TryMakeAbsoluteUrl(new Uri(documentUrl), url, out absoluteUrl);
    }

    public static bool TryMakeAbsoluteUrl(Uri documentUri, string url, [MaybeNullWhen(false)] out string absoluteUrl)
    {
        var documentUrl = new DocumentUrl(documentUri);
        return documentUrl.TryMakeAbsolute(url, out absoluteUrl) && Uri.IsWellFormedUriString(absoluteUrl, UriKind.Absolute);
    }

    public static bool CanParse(Document document)
    {
        return false;
    }

    public static bool TryParse(Document document, [MaybeNullWhen(false)] out Article article)
    {
        return TryParse(document, ReadabilityOptions.Default, out article);
    }

    public static bool TryParse(Document document, Uri documentUri, [MaybeNullWhen(false)] out Article article)
    {
        return TryParse(document, documentUri, ReadabilityOptions.Default, out article);
    }

    public static bool TryParse(Document document, ReadabilityOptions options, [MaybeNullWhen(false)] out Article article)
    {
        return TryParse(document, new DocumentUrl(document), options, out article);
    }

    public static bool TryParse(Document document, Uri documentUri, ReadabilityOptions options, [MaybeNullWhen(false)] out Article article)
    {
        return TryParse(document, new DocumentUrl(documentUri, document), options, out article);
    }

    private static bool TryParse(Document document, DocumentUrl documentUrl, ReadabilityOptions options, [MaybeNullWhen(false)] out Article article)
    {
        var reader = new DocumentReader(document, documentUrl, options);
        return reader.TryParse(out article);
    }
}

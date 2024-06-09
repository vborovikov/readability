namespace Readability;

using System;
using System.Diagnostics.CodeAnalysis;
using Brackets;

public static partial class DocumentExtensions
{
    public static bool TryFindArticle(this Document document, ArticlePath articlePath, [MaybeNullWhen(false)] out Article article)
    {
        return TryFindArticle(document, articlePath, ReadabilityOptions.Default, out article);
    }

    public static bool TryFindArticle(this Document document, ArticlePath articlePath, Uri documentUri, [MaybeNullWhen(false)] out Article article)
    {
        return TryFindArticle(document, articlePath, documentUri, ReadabilityOptions.Default, out article);
    }

    public static bool TryFindArticle(this Document document, ArticlePath articlePath, ReadabilityOptions options, [MaybeNullWhen(false)] out Article article)
    {
        return TryFindArticle(document, articlePath, new DocumentUrl(document), options, out article);
    }

    public static bool TryFindArticle(this Document document, ArticlePath articlePath, Uri documentUri, ReadabilityOptions options, [MaybeNullWhen(false)] out Article article)
    {
        return TryFindArticle(document, articlePath, new DocumentUrl(documentUri, document), options, out article);
    }

    private static bool TryFindArticle(Document document, ArticlePath articlePath, DocumentUrl documentUrl, ReadabilityOptions options, [MaybeNullWhen(false)] out Article article)
    {
        var reader = new DocumentReader(document, documentUrl, options);
        return reader.TryFind(articlePath, out article);
    }

    public static Article FindArticle(this Document document, ArticlePath articlePath)
    {
        return FindArticle(document, articlePath, ReadabilityOptions.Default);
    }

    public static Article FindArticle(this Document document, ArticlePath articlePath, ReadabilityOptions options)
    {
        return FindArticle(document, articlePath, new DocumentUrl(document), options);
    }

    public static Article FindArticle(this Document document, ArticlePath articlePath, Uri documentUri, ReadabilityOptions options)
    {
        return FindArticle(document, articlePath, new DocumentUrl(documentUri), options);
    }

    private static Article FindArticle(Document document, ArticlePath articlePath, DocumentUrl documentUrl, ReadabilityOptions options)
    {
        var reader = new DocumentReader(document, documentUrl, options);
        return reader.Find(articlePath);
    }

    public static bool TryParseArticle(this Document document, [MaybeNullWhen(false)] out Article article)
    {
        return TryParseArticle(document, ReadabilityOptions.Default, out article);
    }

    public static bool TryParseArticle(this Document document, Uri documentUri, [MaybeNullWhen(false)] out Article article)
    {
        return TryParseArticle(document, documentUri, ReadabilityOptions.Default, out article);
    }

    public static bool TryParseArticle(this Document document, ReadabilityOptions options, [MaybeNullWhen(false)] out Article article)
    {
        return TryParseArticle(document, new DocumentUrl(document), options, out article);
    }

    public static bool TryParseArticle(this Document document, Uri documentUri, ReadabilityOptions options, [MaybeNullWhen(false)] out Article article)
    {
        return TryParseArticle(document, new DocumentUrl(documentUri), options, out article);
    }

    private static bool TryParseArticle(Document document, DocumentUrl documentUrl, ReadabilityOptions options, [MaybeNullWhen(false)] out Article article)
    {
        var reader = new DocumentReader(document, documentUrl, options);
        return reader.TryParse(out article);
    }

    public static Article ParseArticle(this Document document)
    {
        return ParseArticle(document, ReadabilityOptions.Default);
    }

    public static Article ParseArticle(this Document document, Uri documentUri)
    {
        return ParseArticle(document, documentUri, ReadabilityOptions.Default);
    }

    public static Article ParseArticle(this Document document, ReadabilityOptions options)
    {
        return ParseArticle(document, new DocumentUrl(document), options);
    }

    public static Article ParseArticle(this Document document, Uri documentUri, ReadabilityOptions options)
    {
        return ParseArticle(document, new DocumentUrl(documentUri), options);
    }

    private static Article ParseArticle(Document document, DocumentUrl documentUrl, ReadabilityOptions options)
    {
        var reader = new DocumentReader(document, documentUrl, options);
        return reader.Parse();
    }
}

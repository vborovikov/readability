namespace Readability;

using System;
using Brackets;

public partial class DocumentReader
{
    public static Article ParseArticle(Document document, Uri documentUri, ReadabilityOptions? options = null)
    {
        var documentClone = document.Clone();
        var reader = new DocumentReader(documentClone, documentUri, options ?? new());
        return reader.Parse();
    }

    public static Article ParseArticle(string documentText, Uri documentUri, ReadabilityOptions? options = null) =>
        ParseArticle(Document.Html.Parse(documentText), documentUri, options);
}

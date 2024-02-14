namespace Readability;

using System;
using System.Diagnostics.CodeAnalysis;
using Brackets;

class DocumentUrl
{
    private readonly Uri baseUri;

    public DocumentUrl(Uri baseUri)
    {
        this.baseUri = baseUri;
    }

    public DocumentUrl(Document document)
    {
        //todo: get the base uri from the document
    }

    public bool TryMakeAbsolute(ReadOnlySpan<char> urlPath, [MaybeNullWhen(false)] out string absoluteUrl)
    {
        //todo: make it absolute
        absoluteUrl = null;
        return false;
    }
}

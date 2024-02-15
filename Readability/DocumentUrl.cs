namespace Readability;

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Brackets;

class DocumentUrl
{
    private readonly string baseUrl;
    private readonly string pathUrl;

    public DocumentUrl(Uri url)
    {
        this.baseUrl = GetBaseUrl(url);
        this.pathUrl = GetPathUrl(url);
    }

    public DocumentUrl(Document document)
    {
        if (!TryFindDocumentUrl(document, out var documentUrl))
            throw new InvalidOperationException();

        this.baseUrl = GetBaseUrl(documentUrl);
        this.pathUrl = GetPathUrl(documentUrl);
    }

    public bool TryMakeAbsolute(ReadOnlySpan<char> url, [MaybeNullWhen(false)] out string absoluteUrl)
    {
        if (url.IsEmpty)
        {
            absoluteUrl = this.baseUrl;
            return true;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            // no scheme
            absoluteUrl = string.Concat(this.baseUrl.AsSpan()[..(this.baseUrl.IndexOf(':') + 1)], url);
            return true;
        }

        if (url[0] == '/')
        {
            // just path, concatenate with the base url
            absoluteUrl = string.Concat(this.baseUrl, url);
            return true;
        }

        if (url.StartsWith("./", StringComparison.Ordinal))
        {
            // current path, concatenate with the path url
            absoluteUrl = string.Concat(this.pathUrl, url[2..]);
            return true;
        }

        if (url[0] == '#')
        {
            // ignore hash URLs
            absoluteUrl = null;
            return false;
        }

        if (DataUrl.TryParse(url, out _))
        {
            // ignore data URLs
            absoluteUrl = null;
            return false;
        }

        if (Uri.IsWellFormedUriString(url.ToString(), UriKind.Absolute))
        {
            // ignore absolute URLs
            absoluteUrl = null;
            return false;
        }

        absoluteUrl = string.Concat(this.pathUrl, url);
        return true;
    }

    private static string GetBaseUrl(Uri uri)
    {
        var url = new StringBuilder(uri.Scheme + "://");

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            url.Append(uri.UserInfo);
            url.Append('@');
        }

        url.Append(uri.Host);

        if (!uri.IsDefaultPort)
        {
            url.Append(':');
            url.Append(uri.Port.ToString(CultureInfo.InvariantCulture));
        }

        return url.ToString();
    }

    private static string GetPathUrl(Uri uri)
    {
        return GetBaseUrl(uri) + uri.AbsolutePath[..(uri.AbsolutePath.LastIndexOf('/') + 1)];
    }

    private static bool TryFindDocumentUrl(Document document, [MaybeNullWhen(false)] out Uri documentUrl)
    {
        if (document.FirstOrDefault<ParentTag>(e => e.Name == "html") is ParentTag html &&
            html.FirstOrDefault<ParentTag>(e => e.Name == "head") is ParentTag head)
        {
            var link = head.FirstOrDefault<Tag>(e => e.Name == "link" && e.Attributes.Has("rel", "canonical"));
            if (link is not null &&
                link.Attributes["href"] is { Length: > 0 } href &&
                Uri.TryCreate(href.ToString(), UriKind.Absolute, out var canonicalUrl))
            {
                documentUrl = canonicalUrl;
                return true;
            }

            var meta = head.FirstOrDefault<Tag>(e => e.Name == "meta" && e.Attributes.Has("property", "og:url"));
            if (meta is not null &&
                meta.Attributes["content"] is { Length: > 0 } content &&
                Uri.TryCreate(content.ToString(), UriKind.Absolute, out var ogUrl))
            {
                documentUrl = ogUrl;
                return true;
            }
        }

        documentUrl = null;
        return false;
    }
}

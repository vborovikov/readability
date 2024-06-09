namespace Readability;

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Brackets;

sealed class DocumentUrl
{
    private readonly string baseUrl;
    private readonly string pathUrl;
    private readonly Uri pathUri;
    private readonly bool hasBase;

    public DocumentUrl(Uri uri, Document? document = null)
    {
        this.baseUrl = GetBaseUrl(uri);
        this.pathUrl = GetPathUrl(uri);
        this.pathUri = new Uri(this.pathUrl);
        this.hasBase = false;

        if (document is not null &&
            TryFindBaseUrl(document, out var docBaseUrl) &&
            TryMakeAbsolute(docBaseUrl, out var newUrl) &&
            Uri.TryCreate(newUrl, UriKind.Absolute, out var newUri))
        {
            this.baseUrl = GetBaseUrl(newUri);
            this.pathUrl = GetPathUrl(newUri);
            this.pathUri = new Uri(this.pathUrl);
            this.hasBase = true;
        }
    }

    public DocumentUrl(Document document)
    {
        if (!TryFindDocumentUri(document, out var documentUrl))
            throw new DocumentUrlNotFound();

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
            if (this.hasBase && Uri.TryCreate(this.pathUri, url.ToString(), out var hashUri))
            {
                absoluteUrl = hashUri.ToString();
                return true;
            }

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

        var urlStr = url.ToString();
        if (Uri.TryCreate(urlStr, UriKind.Absolute, out var urlObj))
        {
            urlStr = urlObj.ToString();
            if (urlStr.Length > url.Length)
            {
                // return a better form url
                absoluteUrl = urlStr;
                return true;
            }

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

    private static bool TryFindBaseUrl(Document document, [MaybeNullWhen(false)] out string baseUrl)
    {
        var baze = document
            .FirstOrDefault<ParentTag>(e => e.Name == "html")?
            .FirstOrDefault<ParentTag>(e => e.Name == "head")?
            .FirstOrDefault<Tag>(e => e.Name == "base" && e.Attributes.Has("href"));
        if (baze is not null && baze.Attributes["href"] is { Length: > 0 } href)
        {
            baseUrl = href.ToString();
            return true;
        }

        baseUrl = null;
        return false;
    }

    private static bool TryFindDocumentUri(Document document, [MaybeNullWhen(false)] out Uri documentUrl)
    {
        if (document.FirstOrDefault<ParentTag>(e => e.Name == "html") is ParentTag html &&
            html.FirstOrDefault<ParentTag>(e => e.Name == "head") is ParentTag head)
        {
            var link = head.FirstOrDefault<Tag>(e => e.Name == "link" && e.Attributes.Has("rel", "canonical"));
            if (link is not null && link.Attributes["href"] is { Length: > 0 } href &&
                Uri.TryCreate(href.ToString(), UriKind.Absolute, out var canonicalUrl))
            {
                documentUrl = canonicalUrl;
                return true;
            }

            var meta = head.FirstOrDefault<Tag>(e => e.Name == "meta" && e.Attributes.Has("property", "og:url"));
            if (meta is not null && meta.Attributes["content"] is { Length: > 0 } content &&
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

public static class UrlExtensions
{
    public static bool TryMakeAbsoluteUrl(this Uri documentUri, string url, [MaybeNullWhen(false)] out string absoluteUrl)
    {
        var documentUrl = new DocumentUrl(documentUri);
        return documentUrl.TryMakeAbsolute(url, out absoluteUrl) && Uri.IsWellFormedUriString(absoluteUrl, UriKind.Absolute);
    }
}

[Serializable]
public class DocumentUrlNotFound : Exception
{
    public DocumentUrlNotFound()
    {
    }

    public DocumentUrlNotFound(string? message) : base(message)
    {
    }

    public DocumentUrlNotFound(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
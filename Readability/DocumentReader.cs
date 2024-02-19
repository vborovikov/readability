namespace Readability;

using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Brackets;
using FuzzyCompare;
using FuzzyCompare.Text;

public record ReadabilityOptions
{
    public int? MaxElemsToParse { get; init; }
    public int? NbTopCandidates { get; init; }
    public int? CharThreshold { get; init; }
    public string[] ClassesToPreserve { get; init; } = [];
    public bool? KeepClasses { get; init; }
    public bool? DisableJsonLD { get; init; }
}

public class DocumentReader
{
    private static readonly SearchValues<char> HierarchicalSeparators = SearchValues.Create("|-\\/>»");

    // See: https://schema.org/Article
    private static readonly string[] JsonLDArticleTypes =
    [
        "Article",
        "AdvertiserContentArticle",
        "NewsArticle",
        "AnalysisNewsArticle",
        "AskPublicNewsArticle",
        "BackgroundNewsArticle",
        "OpinionNewsArticle",
        "ReportageNewsArticle",
        "ReviewNewsArticle",
        "Report",
        "SatiricalArticle",
        "ScholarlyArticle",
        "MedicalScholarlyArticle",
        "SocialMediaPosting",
        "BlogPosting",
        "LiveBlogPosting",
        "DiscussionForumPosting",
        "TechArticle",
        "APIReference"
    ];

    // The commented out elements qualify as phrasing content but tend to be
    // removed by readability when put into paragraphs, so we ignore them here.
    private static readonly string[] PhrasingTags =
    [
        // "canvas", "iframe", "svg", "video",
        "abbr",
        "audio",
        "b",
        "bdo",
        "br",
        "button",
        "cite",
        "code",
        "data",
        "datalist",
        "dfn",
        "em",
        "embed",
        "i",
        "img",
        "input",
        "kbd",
        "label",
        "mark",
        "math",
        "meter",
        "noscript",
        "object",
        "output",
        "progress",
        "q",
        "ruby",
        "samp",
        "script",
        "select",
        "small",
        "span",
        "strong",
        "sub",
        "sup",
        "textarea",
        "time",
        "var",
        "wbr"
    ];

    private static readonly string[] UnlikelyCandidates =
    [
        "-ad-",
        "ai2html",
        "banner",
        "breadcrumbs",
        "combx",
        "comment",
        "community",
        "cover-wrap",
        "disqus",
        "extra",
        "footer",
        "gdpr",
        "header",
        "legends",
        "menu",
        "related",
        "remark",
        "replies",
        "rss",
        "shoutbox",
        "sidebar",
        "skyscraper",
        "social",
        "sponsor",
        "supplemental",
        "ad-break",
        "agegate",
        "pagination",
        "pager",
        "popup",
        "yom-remote"
    ];

    private static readonly string[] MaybeCandidates =
    [
        "and",
        "article",
        "body",
        "column",
        "content",
        "main",
        "shadow"
    ];

    private static readonly string[] UnlikelyRoles =
    [
        "menu",
        "menubar",
        "complementary",
        "navigation",
        "alert",
        "alertdialog",
        "dialog"
    ];

    private static readonly string[] Bylines = ["byline", "author", "dateline", "writtenby", "p-author"];

    private static readonly string[] DefaultTagsToScore = ["section", "h2", "h3", "h4", "h5", "h6", "p", "td", "pre"];

    private static readonly string[] DivToParaElems = ["blockquote", "dl", "div", "img", "ol", "p", "pre", "table", "ul"];

    private static readonly string[] AlterToDivExceptions = ["div", "article", "section", "p"];

    // Commas as used in Latin, Sindhi, Chinese and various other scripts.
    // see: https://en.wikipedia.org/wiki/Comma#Comma_variants
    private static readonly SearchValues<char> Commas = SearchValues.Create("\u002C\u060C\uFE50\uFE10\uFE11\u2E41\u2E34\u2E32\uFF0C");

    private static readonly string[] PositiveNames =
    [
        "article",
        "body",
        "content",
        "entry",
        "hentry",
        "h-entry",
        "main",
        "page",
        "pagination",
        "post",
        "text",
        "blog",
        "story"
    ];

    private static readonly string[] NegativeNames =
    [
        "-ad-",
        "hidden",
        "hid",
        "banner",
        "combx",
        "comment",
        "com-",
        "contact",
        "foot",
        "footer",
        "footnote",
        "gdpr",
        "masthead",
        "media",
        "meta",
        "outbrain",
        "promo",
        "related",
        "scroll",
        "share",
        "shoutbox",
        "sidebar",
        "skyscraper",
        "sponsor",
        "shopping",
        "tags",
        "tool",
        "widget"
    ];

    private static readonly string[] PresentationalAttributes =
        ["align", "background", "bgcolor", "border", "cellpadding", "cellspacing", "frame", "hspace", "rules", "style", "valign", "vspace"];

    private static readonly string[] DeprecatedSizeAttributeElems = ["table", "th", "td", "hr", "pre"];

    // If the table has a descendant with any of these tags, consider a data table:
    private static readonly string[] DataTableDescendants = ["col", "colgroup", "tfoot", "thead", "th"];

    private static readonly string[] AllowedVideoHosts =
    [
        "dailymotion.com",
        "youtube.com",
        "youtube-nocookie.com",
        "player.vimeo.com",
        "v.qq.com",
        "archive.org",
        "upload.wikimedia.org",
        "player.twitch.tv",
    ];

    private static readonly string[] ShareElements = ["share", "sharedaddy"];

    // These are the classes that readability sets itself.
    private static readonly string[] DefaultClassesToPreserve = ["page"];

    // The number of top candidates to consider when analysing how
    // tight the competition is among candidates.
    private const int DefaultNTopCandidates = 5;

    // The default number of chars an article must have in order to return a result
    private const int DefaultCharThreshold = 500;

    [Flags]
    private enum CleanFlags
    {
        None = 0,

        StripUnlikelys = 0x1,
        WeightClasses = 0x2,
        CleanConditionally = 0x4,

        All = StripUnlikelys | WeightClasses | CleanConditionally,
    }

    private record Attempt(ParentTag ArticleContent, int TextLength);

    private readonly Document document;
    private readonly DocumentUrl documentUrl;
    private CleanFlags flags;
    private string? articleLang;
    private string? articleByline;
    private string? articleTitle;
    private string? articleDir;
    private readonly int nbTopCandidates;
    private readonly int charThreshold;
    private readonly bool keepClasses;
    private readonly string[] classesToPreserve;
    private readonly List<Attempt> attempts;

    public DocumentReader(Document document, ReadabilityOptions? options = null)
        : this(document, new DocumentUrl(document), options)
    {
    }

    public DocumentReader(Document document, Uri documentUri, ReadabilityOptions? options = null)
        : this(document, new DocumentUrl(documentUri), options)
    {
    }

    private DocumentReader(Document document, DocumentUrl documentUrl, ReadabilityOptions? options)
    {
        this.document = document;
        this.documentUrl = documentUrl;
        this.flags = CleanFlags.All;

        this.nbTopCandidates = options?.NbTopCandidates ?? DefaultNTopCandidates;
        this.charThreshold = options?.CharThreshold ?? DefaultCharThreshold;
        this.keepClasses = options?.KeepClasses ?? false;
        this.classesToPreserve = [.. DefaultClassesToPreserve, .. (options?.ClassesToPreserve ?? [])];

        this.attempts = [];
    }

    public bool CanRead => true;

    public Article Parse()
    {
        UnwrapNoscriptImages();
        var jsonMetadata = GetJsonLD();
        RemoveScripts();
        PrepareDocument();
        var metadata = GetArticleMetadata(jsonMetadata);
        this.articleTitle = metadata.Title;
        var articleContent = GrabArticle() ?? throw new ArticleNotFoundException();
        PostProcessContent(articleContent);

        return new Article
        {
            Title = this.articleTitle,
            Byline = metadata.Byline ?? this.articleByline,
            Excerpt = metadata.Excerpt ?? GetArticleExcerpt(articleContent),
            Content = articleContent,
            SiteName = metadata.SiteName,
            PublishedTime = metadata.Published,
            Language = this.articleLang,
            Dir = this.articleDir,
        };
    }

    private void UnwrapNoscriptImages()
    {
        foreach (var img in this.document.FindAll<Tag>(t => t.Name == "img").ToArray())
        {
            if (img.Attributes.Any(at => at.Name == "src" || at.Name == "srcset" ||
                at.Name == "data-src" || at.Name == "data-srcset" || at.ValueHasImage()))
            {
                continue;
            }

            img.Remove();
        }

        foreach (var noscript in this.document.FindAll<ParentTag>(t => t.Name == "noscript").ToArray())
        {
            var tmp = noscript.FirstOrDefault()?.Clone();
            if (tmp is null)
                continue;

            if (FindSingleImage(tmp) is not Tag newImg)
                continue;

            var prevElement = noscript.PreviousSiblingOrDefault();
            if (prevElement is null || FindSingleImage(prevElement) is not Tag prevImg)
                continue;

            foreach (var attr in prevImg.Attributes)
            {
                if (!attr.HasValue)
                    continue;

                if (attr.Name == "src" || attr.Name == "srcset" || attr.ValueHasImage())
                {
                    if (newImg.Attributes[attr.Name].Equals(attr.Value, StringComparison.Ordinal))
                        continue;

                    var attrName = attr.Name;
                    if (newImg.Attributes.Has(attrName))
                    {
                        attrName = "data-old-" + attrName;
                    }

                    newImg.Attributes.Set(attrName, attr.Value);
                }
            }

            if (noscript.Parent is ParentTag container)
                container.Replace(prevElement, tmp);
        }

        static Tag? FindSingleImage(Element element)
        {
            return element switch
            {
                ParentTag parent when parent.FindAll(el => el is Tag { Name: "img" }).ToArray() is [Tag singleChildImg] => singleChildImg,
                Tag { Name: "img" } elementAsImg => elementAsImg,
                _ => null
            };
        }
    }

    private ArticleMetadata? GetJsonLD()
    {
        foreach (var jsonld in this.document.FindAll<ParentTag>(
            t => t.Name == "script" && t.Attributes["type"].Equals("application/ld+json", StringComparison.Ordinal)))
        {
            try
            {
                if (jsonld.FirstOrDefault(el => el is Content) is not Content content)
                    continue;

                JsonElement parsed = JsonDocument.Parse(content.Data.Trim().Trim(';').Trim().ToString()).RootElement;
                if (!parsed.TryGetString("@context", out var context) ||
                    !context.EndsWith("://schema.org", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!parsed.TryGetProperty("@type", out _) &&
                    parsed.TryGetProperty("@graph", out var graphProp) &&
                    graphProp.ValueKind == JsonValueKind.Array)
                {
                    var articleTypeFound = false;

                    foreach (var graphItem in graphProp.EnumerateArray())
                    {
                        if (graphItem.TryGetProperty("@type", out var grapItemType) &&
                            JsonLDArticleTypes.Contains(grapItemType.GetString()))
                        {
                            articleTypeFound = true;
                            parsed = graphItem;
                            break;
                        }
                    }

                    if (!articleTypeFound)
                        continue;
                }

                if (!parsed.TryGetString("@type", out var parsedType) ||
                    !JsonLDArticleTypes.Contains(parsedType))
                {
                    continue;
                }

                var metadata = new ArticleMetadata();

                if (parsed.TryGetString("name", out var parsedName) &&
                    parsed.TryGetString("headline", out var parsedHeadline) &&
                    parsedName != parsedHeadline)
                {
                    // we have both name and headline element in the JSON-LD. They should both be the same but some websites like aktualne.cz
                    // put their own name into "name" and the article title to "headline" which confuses Readability. So we try to check if either
                    // "name" or "headline" closely matches the html title, and if so, use that one. If not, then we use "name" by default.

                    var title = GetArticleTitle();
                    var nameMatches = ComparisonMethods.JaroWinklerSimilarity(parsedName, title) > 0.75f;
                    var headlineMatches = ComparisonMethods.JaroWinklerSimilarity(parsedHeadline, title) > 0.75f;

                    metadata.Title = headlineMatches && !nameMatches ? parsedHeadline : parsedName;
                }
                else if (parsed.TryGetString("name", out var parsedNameOnly))
                {
                    metadata.Title = parsedNameOnly;
                }
                else if (parsed.TryGetString("headline", out var parsedHeadlineOnly))
                {
                    metadata.Title = parsedHeadlineOnly;
                }

                if (parsed.TryGetProperty("author", out var authorProp))
                {
                    if (authorProp.TryGetString("name", out var oneAuthorName))
                    {
                        metadata.Byline = oneAuthorName;
                    }
                    else if (authorProp.ValueKind == JsonValueKind.Array)
                    {
                        var authors = new StringBuilder();
                        foreach (var author in authorProp.EnumerateArray())
                        {
                            if (author.TryGetString("name", out var authorName))
                            {
                                if (authors.Length > 0)
                                {
                                    authors.Append(", ");
                                }

                                authors.Append(authorName);
                            }
                        }

                        metadata.Byline = authors.ToString();
                    }
                }

                if (parsed.TryGetString("description", out var parsedDescription))
                {
                    metadata.Excerpt = parsedDescription;
                }
                else if (parsed.TryGetString("summary", out var parsedSummary))
                {
                    metadata.Excerpt = parsedSummary;
                }

                if (parsed.TryGetProperty("publisher", out var publisherProp) &&
                    publisherProp.TryGetString("name", out var publisherName))
                {
                    metadata.SiteName = publisherName;
                }
                else if (parsed.TryGetProperty("creator", out var creatorProp) &&
                    creatorProp.TryGetString("name", out var creatorName))
                {
                    metadata.SiteName = creatorName;
                }

                if (parsed.TryGetProperty("datePublished", out var datePublishedProp) &&
                    datePublishedProp.TryGetDateTimeOffset(out var datePublished))
                {
                    metadata.Published = datePublished;
                }
                else if (parsed.TryGetProperty("dateCreated", out var dateCreatedProp) &&
                    dateCreatedProp.TryGetDateTimeOffset(out var dateCreated))
                {
                    metadata.Published = dateCreated;
                }

                return metadata;
            }
            catch
            {
            }
        }

        return null;
    }

    private string? GetArticleTitle()
    {
        var origTitle = string.Empty;

        if (this.document.Find<ParentTag>(t => t.Name == "title") is ParentTag titleTag &&
            titleTag.FirstOrDefault() is Content titleText)
        {
            origTitle = titleText.Data.ToTrimString();
        }

        var titleTokens = origTitle.Tokenize().ToArray().AsSpan();
        ReadOnlySpan<Token> curTitle = titleTokens;
        var separatorIndex = titleTokens.IndexOf(3, tks =>
        {
            if (tks[0].Category != TokenCategory.WhiteSpace || tks[^1].Category != TokenCategory.WhiteSpace)
                return false;

            return tks[1].Span.Length == 1 && HierarchicalSeparators.Contains(tks[1].Span[0]);
        });

        var titleHadHierarchicalSeparators = separatorIndex > 0;
        if (titleHadHierarchicalSeparators)
        {
            var beforeTokens = titleTokens[..separatorIndex];
            var beforeWordCount = CountWords(beforeTokens);
            var afterTokens = titleTokens[(separatorIndex + 3)..];
            var afterWordCount = CountWords(afterTokens);

            curTitle = beforeWordCount > afterWordCount ? beforeTokens : afterTokens;
        }
        else
        {
            separatorIndex = titleTokens.LastIndexOf(2, tks => tks[0] == ':' && tks[1].Category == TokenCategory.WhiteSpace);
            if (separatorIndex > 0)
            {
                // Check if we have an heading containing this exact string, so we
                // could assume it's the full title.
                var headings = this.document.FindAll<ParentTag>(t => t.Name == "h1" || t.Name == "h2");
                if (!headings.Any(h => h.FirstOrDefault() is Content hText &&
                    hText.Data.JaroWinklerSimilarity(origTitle) > 0.9f))
                {
                    curTitle = titleTokens[(separatorIndex + 2)..];
                    if (CountWords(curTitle) <= 3)
                    {
                        // If the title is now too short, try the first colon instead:
                        separatorIndex = titleTokens.IndexOf(2, tks => tks[0] == ':' && tks[1].Category == TokenCategory.WhiteSpace);
                        curTitle = titleTokens[(separatorIndex + 2)..];
                    }
                    else if (CountWords(titleTokens[..titleTokens.IndexOf(tk => tk == ':')]) > 5)
                    {
                        // But if we have too many words before the colon there's something weird
                        // with the titles and the H tags so let's just use the original title instead
                        curTitle = titleTokens;
                    }
                }
            }
            else if (origTitle is { Length: < 15 or > 150 })
            {
                var hOnes = this.document.FindAll<ParentTag>(t => t.Name == "h1").ToArray();
                if (hOnes is [ParentTag hOne] && hOne.FirstOrDefault() is Content hOneText)
                {
                    curTitle = hOneText.Data.ToTrimString().Tokenize().ToArray().AsSpan();
                }
            }
        }

        var curTitleWordCount = CountWords(curTitle);
        if (curTitleWordCount <= 4 && (!titleHadHierarchicalSeparators || curTitleWordCount != CountWords(titleTokens) - 1))
        {
            return origTitle;
        }

        var title = new StringBuilder(origTitle.Length);
        Token prevToken = default;
        foreach (var token in curTitle)
        {
            if (token.Category == prevToken.Category && token.Category == TokenCategory.WhiteSpace)
                continue;

            title.Append(token.Span);
            prevToken = token;
        }
        return title.ToString();

        static int CountWords(ReadOnlySpan<Token> tokens)
        {
            var count = 0;
            for (var i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Category == TokenCategory.Word)
                    count++;
            }
            return count;
        }
    }

    private void RemoveScripts()
    {
        foreach (var scriptTag in this.document.FindAll<Tag>(t => t.Name == "script" || t.Name == "noscript").ToArray())
        {
            scriptTag.Remove();
        }
    }

    private void PrepareDocument()
    {
        // remove comments
        foreach (var comment in this.document.FindAll(e => e is Comment).ToArray())
        {
            comment.Remove();
        }

        // Remove all style tags in head
        foreach (var styleTag in this.document.FindAll<Tag>(t => t.Name == "style").ToArray())
        {
            styleTag.Remove();
        }

        if (this.document.Find<ParentTag>(el => el.Name == "body") is ParentTag body)
        {
            ReplaceBrs(body);
            ReplaceTags(body, "font", "span");
        }
    }

    private static void ReplaceBrs(ParentTag elem)
    {
        var brs = elem.FindAll(t => t is Tag { Name: "br" }).Cast<Tag>().ToArray();
        for (var i = 0; i < brs.Length; i++)
        {
            var br = brs[i];
            var next = br.NextSiblingOrDefault();

            // Whether 2 or more <br> elements have been found and replaced with a
            // <p> block.
            var replaced = false;

            // If we find a <br> chain, remove the <br>s until we hit another node
            // or non-whitespace. This leaves behind the first <br> in the chain
            // (which will be replaced with a <p> later).
            while ((next = next.NextElement()) is Tag { Name: "br" })
            {
                replaced = true;
                var brSibling = next.NextSiblingOrDefault();
                next.Remove();
                next = brSibling;
            }

            // If we removed a <br> chain, replace the remaining <br> with a <p>. Add
            // all sibling nodes as children of the <p> until we hit another <br>
            // chain.
            if (replaced)
            {
                var p = (ParentTag)Document.Html.CreateTag("p");
                br.ReplaceWith(p);

                next = next?.NextSiblingOrDefault();
                while (next is not null)
                {
                    // If we've hit another <br><br>, we're done adding children to this <p>.
                    if (next is Tag { Name: "br" })
                    {
                        var nextElem = next.NextSiblingOrDefault()?.NextElement();
                        if (nextElem is Tag { Name: "br" })
                            break;
                    }

                    if (!IsPhrasingContent(next))
                        break;

                    // Otherwise, make this node a child of the new <p>.
                    var sibling = next.NextSiblingOrDefault();
                    next.Remove();
                    p.Add(next);
                    next = sibling;
                }

                while (p.LastOrDefault() is CharacterData content && content.Data.IsWhiteSpace())
                {
                    p.Remove(content);
                }

                if (p.Parent?.Name == "p")
                {
                    ChangeTagName(p.Parent, "div");
                }
            }
        }
    }

    private static bool IsPhrasingContent(Element element)
    {
        return element switch
        {
            CharacterData => true,
            ParentTag { Name: "a" or "del" or "ins" } tag => tag.All(IsPhrasingContent),
            Tag someTag => PhrasingTags.Contains(someTag.Name),
            _ => false
        };
    }

    private static void ReplaceTags(ParentTag root, string oldTagName, string newTagName)
    {
        foreach (var oldTag in root.FindAll<ParentTag>(t => t.Name == oldTagName).ToArray())
        {
            ChangeTagName(oldTag, newTagName);
        }
    }

    // _setNodeTag
    private static ParentTag ChangeTagName(ParentTag tag, string newName)
    {
        if (tag.Parent is not ParentTag parent)
            throw new InvalidOperationException();

        var newTag = (ParentTag)Document.Html.CreateTag(newName);

        foreach (var attribute in tag.EnumerateAttributes())
        {
            tag.RemoveAttribute(attribute);
            newTag.AddAttribute(attribute);
        }
        foreach (var element in tag)
        {
            tag.Remove(element);
            newTag.Add(element);
        }
        parent.Replace(tag, newTag);

        return newTag;
    }

    private ArticleMetadata GetArticleMetadata(ArticleMetadata? jsonMetadata)
    {
        var metas = this.document.FindAll<Tag>(t => t.Name == "meta");
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var meta in metas)
        {
            var content = meta.Attributes["content"].Trim();
            if (content.IsEmpty)
                continue;

            var property = meta.Attributes["property"].Trim();
            if (!property.IsEmpty)
            {
                var contentStr = content.ToString();
                foreach (var propertyName in property.EnumerateValues())
                {
                    values[ToCleanString(propertyName)] = contentStr;
                }
            }
            else
            {
                var name = meta.Attributes["name"].Trim();
                if (!name.IsEmpty)
                {
                    values[ToCleanString(name)] = content.ToString();
                }
            }
        }

        return new ArticleMetadata
        {
            Title = WebUtility.HtmlDecode(FindLongestString(
                jsonMetadata?.Title,
                values.GetValueOrDefault("dc:title"),
                values.GetValueOrDefault("dcterm:title"),
                values.GetValueOrDefault("og:title"),
                values.GetValueOrDefault("weibo:article:title"),
                values.GetValueOrDefault("weibo:webpage:title"),
                values.GetValueOrDefault("title"),
                values.GetValueOrDefault("twitter:title")) ??
                GetArticleTitle()),

            Byline = WebUtility.HtmlDecode(FindLongestString(
                jsonMetadata?.Byline,
                values.GetValueOrDefault("dc:creator"),
                values.GetValueOrDefault("dcterm:creator"),
                values.GetValueOrDefault("author"))),

            Excerpt = WebUtility.HtmlDecode(FindLongestString(
                jsonMetadata?.Excerpt,
                values.GetValueOrDefault("dc:description"),
                values.GetValueOrDefault("dcterm:description"),
                values.GetValueOrDefault("og:description"),
                values.GetValueOrDefault("weibo:article:description"),
                values.GetValueOrDefault("weibo:webpage:description"),
                values.GetValueOrDefault("description"),
                values.GetValueOrDefault("twitter:description"))),

            SiteName = WebUtility.HtmlDecode(FindLongestString(
                jsonMetadata?.SiteName, values.GetValueOrDefault("og:site_name"))),

            Published = jsonMetadata?.Published ??
                (DateTimeOffset.TryParse(WebUtility.HtmlDecode(
                    values.GetValueOrDefault("article:published_time") ??
                    values.GetValueOrDefault("parsely-pud-date") ??
                    values.GetValueOrDefault("article:modified_time")),
                    out var dateTime) ? dateTime : null),
        };

        static string ToCleanString(ReadOnlySpan<char> value)
        {
            var builder = new StringBuilder();

            foreach (var token in value.EnumerateTokens())
            {
                if (token.Span.Length == 1 && token.Span[0] == '.')
                    builder.Append(':');
                else if (token.Category != TokenCategory.WhiteSpace)
                    builder.Append(token.Span);
            }

            return builder.ToString();
        }

        static string? FindLongestString(params string?[] strings)
        {
            string? longestString = null;

            foreach (var str in strings)
            {
                if (str is not null && (longestString is null || str.Length > longestString.Length))
                {
                    longestString = str;
                }
            }

            return longestString;
        }
    }

    private ParentTag? GrabArticle(ParentTag? page = null)
    {
        Debug.WriteLine("**** grabArticle ****");

        var isPaging = page is not null;
        page ??= this.document.Find<ParentTag>(t => t.Name == "body");
        if (page is null)
        {
            Debug.WriteLine("No body found in document. Abort.");
            return null;
        }

        var pageCacheHtml = (ParentTag)page.Clone();

        while (true)
        {
            Debug.WriteLine("Starting grabArticle loop");
            var stripUnlikelyCandidates = this.flags.HasFlag(CleanFlags.StripUnlikelys);

            // First, node prepping. Trash nodes that look cruddy (like ones with the
            // class name "comment", etc), and turn divs into P tags where they have been
            // used inappropriately (as in, where they contain no other block level elements.)
            var elementsToScore = new List<Element>();
            var node = this.document.FirstOrDefault(el => el is ParentTag) as Tag;

            var shouldRemoveTitleHeader = true;

            while (node is not null)
            {
                if (node.Name == "html")
                {
                    this.articleLang = node.Attributes["lang"].ToString();
                }

                var matchString = string.Concat(node.Attributes["class"], " ", node.Attributes["id"]);
                if (!IsProbablyVisible(node))
                {
                    Debug.WriteLine($"Removing hidden node - {matchString}");
                    node = node.RemoveAndGetNextTag();
                    continue;
                }

                // User is not able to see elements applied with both "aria-modal = true" and "role = dialog"
                if (node.Attributes.Has("aria-modal", "true") && node.Attributes.Has("role", "dialog"))
                {
                    node = node.RemoveAndGetNextTag();
                    continue;
                }

                // Check to see if this node is a byline, and remove it if it is.
                if (CheckByline(node, matchString))
                {
                    node = node.RemoveAndGetNextTag();
                    continue;
                }

                if (shouldRemoveTitleHeader && HeaderDuplicatesTitle(node))
                {
                    Debug.WriteLine($"Removing header: '{node.ToString().Trim()}' -- '{this.articleTitle?.Trim()}'");
                    shouldRemoveTitleHeader = false;
                    node = node.RemoveAndGetNextTag();
                    continue;
                }

                // Remove unlikely candidates
                if (stripUnlikelyCandidates)
                {
                    if (UnlikelyCandidates.Any(c => matchString.Contains(c, StringComparison.OrdinalIgnoreCase)) &&
                        !MaybeCandidates.Any(c => matchString.Contains(c, StringComparison.OrdinalIgnoreCase)) &&
                        node.FindAncestor(p => p is { Name: "table" or "code" }) is null &&
                        node is not { Name: "body" or "a" })
                    {
                        Debug.WriteLine($"Removing unlikely candidate - {matchString}");
                        node = node.RemoveAndGetNextTag();
                        continue;
                    }

                    var role = node.Attributes["role"].ToString();
                    if (role.Length > 0 && UnlikelyRoles.Contains(role))
                    {
                        Debug.WriteLine($"Removing content with role '{role}' - {matchString}");
                        node = node.RemoveAndGetNextTag();
                        continue;
                    }
                }

                // Remove DIV, SECTION, and HEADER nodes without any content(e.g. text, image, video, or iframe).
                if (node is { Name: "div" or "section" or "header" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" } &&
                    IsElementWithoutContent(node))
                {
                    node = node.RemoveAndGetNextTag();
                    continue;
                }

                if (DefaultTagsToScore.Contains(node.Name))
                {
                    elementsToScore.Add(node);
                }

                // Turn all divs that don't have children block level elements into p's
                if (node is ParentTag { Name: "div" } div)
                {
                    // Put phrasing content into paragraphs.
                    ParentTag? p = null;
                    var childNode = div.FirstOrDefault();
                    while (childNode is not null)
                    {
                        var nextSibling = childNode.NextSiblingOrDefault();
                        if (IsPhrasingContent(childNode))
                        {
                            if (p is not null)
                            {
                                childNode.Remove();
                                p.Add(childNode);
                            }
                            else if (!IsWhiteSpace(childNode))
                            {
                                p = (ParentTag)Document.Html.CreateTag("p");
                                div.Replace(childNode, p);
                                p.Add(childNode);
                            }
                        }
                        else if (p is not null)
                        {
                            while (p.LastOrDefault() is Element element && IsWhiteSpace(element))
                            {
                                p.Remove(element);
                            }
                            p = null;
                        }
                        childNode = nextSibling;
                    }

                    // Sites like http://mobile.slate.com encloses each paragraph with a DIV
                    // element. DIVs with only a P element inside and no text content can be
                    // safely converted into plain P elements to avoid confusing the scoring
                    // algorithm with DIVs with are, in practice, paragraphs.
                    if (HasSingleTagInsideElement(div, "p") && GetLinkDensity(div) < 0.25f)
                    {
                        var newNode = (ParentTag)div.First();
                        div.Remove(newNode);
                        div.Parent?.Replace(div, newNode);
                        node = newNode;
                        elementsToScore.Add(node);
                    }
                    else if (!HasChildBlockElement(div))
                    {
                        node = ChangeTagName(div, "p");
                        elementsToScore.Add(node);
                    }
                }

                node = node.NextTagOrDefault();
            }

            /**
             * Loop through all paragraphs, and assign a score to them based on how content-y they look.
             * Then add their score to their parent node.
             *
             * A score is determined by things like number of commas, class names, etc. Maybe eventually link density.
            **/
            var candidates = new Dictionary<ParentTag, float>();
            foreach (var elementToScore in elementsToScore)
            {
                if (elementToScore.Parent is null)
                    continue;

                // If this paragraph is less than 25 characters, don't even count it.
                var innerText = GetInnerText(elementToScore);
                if (innerText.Length < 25)
                    continue;

                // Exclude nodes with no ancestor.
                var ancestors = elementToScore.EnumerateAncestors().ToArray();
                if (ancestors.Length == 0)
                    continue;

                var contentScore = 0f;

                // Add a point for the paragraph itself as a base.
                contentScore += 1f;

                // Add points for any commas within this paragraph.
                contentScore += innerText.Count(Commas.Contains);

                // For every 100 characters in this paragraph, add another point. Up to 3 points.
                contentScore += Math.Min(innerText.Length / 100f, 3f);

                // Initialize and score ancestors.
                for (var level = 0; level < ancestors.Length; ++level)
                {
                    var ancestor = ancestors[level];
                    ref var ancestorScore = ref CollectionsMarshal.GetValueRefOrAddDefault(candidates, ancestor, out var exists);
                    if (!exists)
                    {
                        ancestorScore = InitializeScore(ancestor);
                    }

                    // Node score divider:
                    // - parent:             1 (no division)
                    // - grandparent:        2
                    // - great grandparent+: ancestor level * 3
                    var scoreDivider = level == 0 ? 1f : level == 1 ? 2f : level * 3f;
                    ancestorScore += contentScore / scoreDivider;
                }
            }

            // After we've calculated scores, loop through all of the possible
            // candidate nodes we found and find the one with the highest score.
            var topCandidates = new PriorityQueue<ParentTag, float>(this.nbTopCandidates);
            foreach (var candidate in candidates)
            {
                // Scale the final candidates score based on link density. Good content
                // should have a relatively small link density (5% or less) and be mostly
                // unaffected by this operation.
                var candidateScore = candidate.Value * (1f - GetLinkDensity(candidate.Key));
                candidates[candidate.Key] = (int)candidateScore;

                Debug.WriteLine($"Candidate: {candidate.Key.Name} with score {candidateScore}");

                if (topCandidates.Count < this.nbTopCandidates)
                {
                    topCandidates.Enqueue(candidate.Key, candidateScore);
                }
                else
                {
                    topCandidates.EnqueueDequeue(candidate.Key, candidateScore);
                }
            }

            (ParentTag Element, float ContentScore) topCandidate =
                topCandidates.Count > 0 ? topCandidates.UnorderedItems.MaxBy(c => c.Priority) : default;
            var neededToCreateTopCandidate = false;
            var parentOfTopCandidate = default(ParentTag);

            // If we still have no top candidate, just use the body as a last resort.
            // We also have to copy the body node so it is something we can modify.
            if (topCandidate.Element is null || topCandidate.Element.Name == "body")
            {
                // Move all of the page's children into topCandidate
                var newTopCandidate = (ParentTag)Document.Html.CreateTag("div");
                neededToCreateTopCandidate = true;
                // Move everything (not just elements, also text nodes etc.) into the container
                // so we even include text directly in the body:
                while (page.FirstOrDefault() is Element element)
                {
                    Debug.WriteLine($"Moving child out: {element.ToText()}");
                    page.Remove(element);
                    newTopCandidate.Add(element);
                }

                page.Add(newTopCandidate);

                topCandidate = (newTopCandidate, InitializeScore(newTopCandidate));
            }
            else if (topCandidate.Element is not null)
            {
                // Find a better top candidate node if it contains (at least three) nodes which belong to `topCandidates` array
                // and whose scores are quite closed with current `topCandidate` node.
                var alternativeCandidateAncestors = new List<ParentTag[]>();
                foreach (var otherTopCandidate in topCandidates.UnorderedItems.OrderByDescending(c => c.Priority).Skip(1))
                {
                    if (otherTopCandidate.Priority / topCandidate.ContentScore >= 0.75f)
                    {
                        alternativeCandidateAncestors.Add(otherTopCandidate.Element.EnumerateAncestors().ToArray());
                    }
                }

                var MinTopCadidates = 3;
                if (alternativeCandidateAncestors.Count >= MinTopCadidates)
                {
                    parentOfTopCandidate = topCandidate.Element.Parent;
                    while (parentOfTopCandidate is not null and not ParentTag { Name: "body" })
                    {
                        var listsContainingThisAncestor = 0;
                        for (var ancestorIndex = 0;
                            ancestorIndex < alternativeCandidateAncestors.Count && listsContainingThisAncestor < MinTopCadidates;
                            ancestorIndex++)
                        {
                            listsContainingThisAncestor +=
                                alternativeCandidateAncestors[ancestorIndex].Contains(parentOfTopCandidate) ? 1 : 0;
                        }
                        if (listsContainingThisAncestor >= MinTopCadidates)
                        {
                            topCandidate = (parentOfTopCandidate, default);
                            break;
                        }
                        parentOfTopCandidate = parentOfTopCandidate.Parent;
                    }
                }
                if (topCandidate.ContentScore == default)
                {
                    topCandidate = topCandidate with { ContentScore = InitializeScore(topCandidate.Element) };
                }

                // Because of our bonus system, parents of candidates might have scores
                // themselves. They get half of the node. There won't be nodes with higher
                // scores than our topCandidate, but if we see the score going *up* in the first
                // few steps up the tree, that's a decent sign that there might be more content
                // lurking in other places that we want to unify in. The sibling stuff
                // below does some of that - but only if we've looked high enough up the DOM
                // tree.
                parentOfTopCandidate = topCandidate.Element.Parent;
                var lastScore = topCandidate.ContentScore;
                // The scores shouldn't get too low.
                var scoreThreshold = lastScore / 3f;
                while (parentOfTopCandidate is not null and not ParentTag { Name: "body" })
                {
                    if (!candidates.TryGetValue(parentOfTopCandidate, out var parentScore))
                    {
                        parentOfTopCandidate = parentOfTopCandidate.Parent;
                        continue;
                    }
                    if (parentScore < scoreThreshold)
                        break;
                    if (parentScore > lastScore)
                    {
                        // Alright! We found a better parent to use.
                        topCandidate = (parentOfTopCandidate, parentScore);
                        break;
                    }
                    lastScore = parentScore;
                    parentOfTopCandidate = parentOfTopCandidate.Parent;
                }

                // If the top candidate is the only child, use parent instead. This will help sibling
                // joining logic when adjacent content is actually located in parent's sibling node.
                parentOfTopCandidate = topCandidate.Element.Parent;
                while (parentOfTopCandidate is not null and not { Name: "body" } &&
                    parentOfTopCandidate.Count(el => el is Tag) == 1)
                {
                    topCandidate = (parentOfTopCandidate, candidates.GetValueOrDefault(parentOfTopCandidate));
                    parentOfTopCandidate = parentOfTopCandidate.Parent;
                }
                if (topCandidate.ContentScore == default)
                {
                    topCandidate = topCandidate with { ContentScore = InitializeScore(topCandidate.Element) };
                }
            }

            // Now that we have the top candidate, look through its siblings for content
            // that might also be related. Things like preambles, content split by ads
            // that we removed, etc.
            var articleContent = (ParentTag)Document.Html.CreateTag("div");
            if (isPaging)
            {
                articleContent.Attributes["id"] = "readability-content";
            }

            var siblingScoreThreshold = Math.Max(10, topCandidate.ContentScore * 0.2f);
            // Keep potential top candidate's parent node to try to get text direction of it later.
            parentOfTopCandidate = topCandidate.Element!.Parent!;
            var siblings = parentOfTopCandidate!.OfType<ParentTag>().ToArray();

            for (int s = 0, sl = siblings.Length; s < sl; s++)
            {
                var sibling = siblings[s];
                var siblingScore = candidates.GetValueOrDefault(sibling);
                var append = false;

                Debug.WriteLine($"Looking at sibling node: \n{sibling.ToText()}\n {(siblingScore != default ? ("with score " + siblingScore) : "")}");
                Debug.WriteLine($"Sibling has score {(siblingScore != default ? siblingScore : "Unknown")}");

                if (sibling == topCandidate.Element)
                {
                    append = true;
                }
                else
                {
                    var contentBonus = 0f;

                    // Give a bonus if sibling nodes and top candidates have the example same classname
                    if (sibling.Attributes["class"].Equals(topCandidate.Element.Attributes["class"], StringComparison.OrdinalIgnoreCase) &&
                        !topCandidate.Element.Attributes["class"].IsEmpty)
                    {
                        contentBonus += topCandidate.ContentScore * 0.2f;
                    }

                    if (siblingScore != default && ((siblingScore + contentBonus) >= siblingScoreThreshold))
                    {
                        append = true;
                    }
                    else if (sibling.Name == "p")
                    {
                        var linkDensity = GetLinkDensity(sibling);
                        var nodeContent = GetInnerText(sibling);
                        var nodeLength = nodeContent.Length;

                        if (nodeLength > 80 && linkDensity < 0.25f)
                        {
                            append = true;
                        }
                        else if (nodeLength < 80 && nodeLength > 0 && linkDensity == 0 &&
                            (nodeContent.Contains(". ") || nodeContent.EndsWith('.')))
                        {
                            append = true;
                        }
                    }
                }

                if (append)
                {
                    Debug.WriteLine($"Appending node: \n{sibling.ToText()}\n");

                    if (!AlterToDivExceptions.Contains(sibling.Name))
                    {
                        // We have a node that isn't a common block level element, like a form or td tag.
                        // Turn it into a div so it doesn't get filtered out later by accident.
                        Debug.WriteLine($"Altering sibling: \n{sibling.ToText()}\n to div.");

                        sibling = ChangeTagName(sibling, "div");
                    }

                    sibling.Remove();
                    articleContent.Add(sibling);
                    // Fetch children again to make it compatible
                    // with DOM parsers without live collection support.
                    siblings = parentOfTopCandidate!.OfType<ParentTag>().ToArray(); //todo: rework
                    // siblings is a reference to the children array, and
                    // sibling is removed from the array when we call appendChild().
                    // As a result, we must revisit this index since the nodes
                    // have been shifted.
                    s -= 1;
                    sl -= 1;
                }
            }

            Debug.WriteLine($"Article content pre-prep: \n{articleContent.ToText()}\n");
            // So we have all of the content that we need. Now we clean it up for presentation.
            PrepArticle(articleContent);
            Debug.WriteLine($"Article content post-prep: \n{articleContent.ToText()}\n");

            if (neededToCreateTopCandidate)
            {
                // We already created a fake div thing, and there wouldn't have been any siblings left
                // for the previous loop, so there's no point trying to create a new div, and then
                // move all the children over. Just assign IDs and class names here. No need to append
                // because that already happened anyway.
                topCandidate.Element.Attributes["id"] = "readability-page-1";
                topCandidate.Element.Attributes["class"] = "page";
            }
            else
            {
                var div = (ParentTag)Document.Html.CreateTag("div");
                div.Attributes["id"] = "readability-page-1";
                div.Attributes["class"] = "page";
                while (articleContent.FirstOrDefault() is Element firstChild)
                {
                    articleContent.Remove(firstChild);
                    div.Add(firstChild);
                }
                articleContent.Add(div);
            }

            Debug.WriteLine($"Article content after paging: \n{articleContent.ToText()}\n");

            var parseSuccessful = true;

            // Now that we've gone through the full algorithm, check to see if
            // we got any meaningful content. If we didn't, we may need to re-run
            // grabArticle with different flags set. This gives us a higher likelihood of
            // finding the content, and the sieve approach gives us a higher likelihood of
            // finding the -right- content.
            var textLength = articleContent.GetContentLength(true);
            if (textLength < this.charThreshold)
            {
                parseSuccessful = false;
                page = pageCacheHtml;

                if (this.flags.HasFlag(CleanFlags.StripUnlikelys))
                {
                    this.flags &= ~CleanFlags.StripUnlikelys;
                    this.attempts.Add(new(articleContent, textLength));
                }
                else if (this.flags.HasFlag(CleanFlags.WeightClasses))
                {
                    this.flags &= ~CleanFlags.WeightClasses;
                    this.attempts.Add(new(articleContent, textLength));
                }
                else if (this.flags.HasFlag(CleanFlags.CleanConditionally))
                {
                    this.flags &= ~CleanFlags.CleanConditionally;
                    this.attempts.Add(new(articleContent, textLength));
                }
                else
                {
                    this.attempts.Add(new(articleContent, textLength));
                    // No luck after removing flags, just return the longest text we found during the different loops
                    // But first check if we actually have something
                    if (this.attempts.Count == 0)
                    {
                        return null;
                    }

                    articleContent = this.attempts.MaxBy(attempt => attempt.TextLength)?.ArticleContent;
                    if (articleContent is null)
                    {
                        return null;
                    }
                    parseSuccessful = true;
                }
            }

            if (parseSuccessful)
            {
                // Find out text direction from ancestors of final top candidate.
                foreach (ParentTag ancestor in parentOfTopCandidate
                    .EnumerateAncestors()
                    .Prepend(topCandidate.Element)
                    .Prepend(parentOfTopCandidate))
                {
                    var articleDir = ancestor.Attributes["dir"];
                    if (!articleDir.IsEmpty)
                    {
                        this.articleDir = articleDir.ToString();
                        break;
                    }
                }

                return articleContent;
            }
        }

        float InitializeScore(Tag tag)
        {
            var score = tag.Name switch
            {
                "div" => +5f,
                "pre" or "td" or "blockquote" => +3f,
                "address" or "ol" or "ul" or "dl" or "dd" or "dt" or "li" or "form" => -3f,
                "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => -5f,
                _ => 0f,
            };
            score += GetClassWeight(tag);

            return score;
        }
    }

    private void PrepArticle(ParentTag articleContent)
    {
        CleanStyles(articleContent);

        // Check for data tables before we continue, to avoid removing items in
        // those tables, which will often be isolated even though they're
        // visually linked to other content-ful elements (text, images, etc.).
        MarkDataTables(articleContent);

        FixLazyImages(articleContent);

        // Clean out junk from the article content
        CleanConditionally(articleContent, "form");
        CleanConditionally(articleContent, "fieldset");
        Clean(articleContent, "object");
        Clean(articleContent, "embed");
        Clean(articleContent, "footer");
        Clean(articleContent, "link");
        Clean(articleContent, "aside");

        // Clean out elements with little content that have "share" in their id/class combinations from final top candidates,
        // which means we don't remove the top candidates even they have "share".

        var shareElementThreshold = DefaultCharThreshold;
        foreach (var element in articleContent)
        {
            if (element is not Tag e)
                continue;
            var endOfSearchMarkerNode = e.NextTagOrDefault(ignoreSelfAndKids: true);
            var next = e.NextTagOrDefault();
            while (next is not null && next != endOfSearchMarkerNode)
            {
                if ((next.Attributes["class"].HasAnyWord(ShareElements) || next.Attributes["id"].HasAnyWord(ShareElements)) &&
                    next.GetContentLength() < shareElementThreshold)
                {
                    next = next.RemoveAndGetNextTag();
                }
                else
                {
                    next = next.NextTagOrDefault();
                }
            }
        }

        Clean(articleContent, "iframe");
        Clean(articleContent, "input");
        Clean(articleContent, "textarea");
        Clean(articleContent, "select");
        Clean(articleContent, "button");
        CleanHeaders(articleContent);

        // Do these last as the previous stuff may have removed junk
        // that will affect these
        CleanConditionally(articleContent, "table");
        CleanConditionally(articleContent, "ul");
        CleanConditionally(articleContent, "div");

        // replace H1 with H2 as H1 should be only title that is displayed separately
        ReplaceTags(articleContent, "h1", "h2");

        // Remove extra paragraphs
        foreach (var paragraph in articleContent.FindAll<ParentTag>(t => t.Name == "p").ToArray())
        {
            var imgCount = paragraph.Count(t => t is Tag { Name: "img" });
            var embedCount = paragraph.Count(t => t is Tag { Name: "embed" });
            var objectCount = paragraph.Count(t => t is Tag { Name: "object" });
            // At this point, nasty iframes have been removed, only remain embedded video ones.
            var iframeCount = paragraph.Count(t => t is Tag { Name: "iframe" });
            var totalCount = imgCount + embedCount + objectCount + iframeCount;

            if (totalCount == 0 && paragraph.GetContentLength(false) == 0)
            {
                paragraph.Remove();
            }
        }

        foreach (var br in articleContent.FindAll<ParentTag>(t => t.Name == "br").ToArray())
        {
            var next = br.NextSiblingOrDefault().NextElement();
            if (next is ParentTag { Name: "p" })
            {
                br.Remove();
            }
        }

        // Remove single-cell tables
        foreach (var table in articleContent.FindAll<ParentTag>(t => t.Name == "table").ToArray())
        {
            var tbody = table.HasSingleTagInside("tbody") ? (ParentTag)table.First(e => e is ParentTag) : table;
            if (tbody.HasSingleTagInside("tr"))
            {
                var row = (ParentTag)tbody.First(e => e is ParentTag);
                if (row.HasSingleTagInside("td"))
                {
                    var cell = (ParentTag)row.First(e => e is ParentTag);
                    cell = ChangeTagName(cell, cell.All(IsPhrasingContent) ? "p" : "div");
                    cell.Remove();
                    table.ReplaceWith(cell);
                }
            }
        }
    }

    /**
     * Clean out spurious headers from an Element.
     *
     * @param Element
     * @return void
    **/
    private void CleanHeaders(ParentTag articleContent)
    {
        foreach (var heading in articleContent.FindAll<ParentTag>(h => h is { Name: "h1" or "h2" }).ToArray())
        {
            var shouldRemove = GetClassWeight(heading) < 0;
            if (shouldRemove)
            {
                Debug.WriteLine($"Removing header with low class weight: \n{heading.ToText()}\n");
                heading.Remove();
            }
        }
    }

    /**
     * Remove the style attribute on every tag and under.
     *
     * @param Element
     * @return void
    **/
    private static void CleanStyles(ParentTag articleContent)
    {
        foreach (var e in articleContent.FindAll<Tag>(t => t.Name != "svg"))
        {
            // Remove `style` and deprecated presentational attributes
            foreach (var presentationalAttrName in PresentationalAttributes)
            {
                e.TryRemoveAttribute(presentationalAttrName);
            }

            if (DeprecatedSizeAttributeElems.Contains(e.Name))
            {
                e.TryRemoveAttribute("width");
                e.TryRemoveAttribute("height");
            }
        }
    }

    /**
     * Look for 'data' (as opposed to 'layout') tables, for which we use
     * similar checks as
     * https://searchfox.org/mozilla-central/rev/f82d5c549f046cb64ce5602bfd894b7ae807c8f8/accessible/generic/TableAccessible.cpp#19
     */
    private static void MarkDataTables(ParentTag articleContent)
    {
        foreach (var table in articleContent.FindAll<ParentTag>(t => t.Name == "table"))
        {
            if (table.Attributes.Has("role", "presentation"))
            {
                table.Attributes["_readabilityDataTable"] = "false";
                continue;
            }
            if (table.Attributes.Has("datatable", "0"))
            {
                table.Attributes["_readabilityDataTable"] = "false";
                continue;
            }
            if (table.Attributes.Has("summary"))
            {
                table.Attributes["_readabilityDataTable"] = "true";
                continue;
            }

            var caption = table.Find<ParentTag>(t => t.Name == "caption");
            if (caption is not null && caption.Any())
            {
                table.Attributes["_readabilityDataTable"] = "true";
                continue;
            }

            // If the table has a descendant with any of these tags, consider a data table:
            if (table.Find<Tag>(t => DataTableDescendants.Contains(t.Name)) is not null)
            {
                Debug.WriteLine("Data table because found data-y descendant");
                table.Attributes["_readabilityDataTable"] = "true";
                continue;
            }

            // Nested tables indicate a layout table:
            if (table.Find<Tag>(t => t.Name == "table") is not null)
            {
                table.Attributes["_readabilityDataTable"] = "false";
                continue;
            }

            var sizeInfo = GetRowAndColumnCount(table);
            if (sizeInfo.Rows >= 10 || sizeInfo.Columns > 4)
            {
                table.Attributes["_readabilityDataTable"] = "true";
                continue;
            }
            // Now just go by size entirely:
            table.Attributes["_readabilityDataTable"] = sizeInfo.Rows * sizeInfo.Columns > 10 ? "true" : "false";
        }
    }

    private static (int Rows, int Columns) GetRowAndColumnCount(ParentTag table)
    {
        var rows = 0;
        var columns = 0;
        foreach (var tr in table.FindAll<ParentTag>(t => t.Name == "tr"))
        {
            var rowspan = 0;
            if (int.TryParse(tr.Attributes["rowspan"], CultureInfo.InvariantCulture, out var rs))
            {
                rowspan = rs;
            }
            rows += Math.Max(rowspan, 1);

            // Now look for column-related info
            var columnsInThisRow = 0;
            foreach (var td in tr.FindAll<Tag>(t => t.Name == "td"))
            {
                var colspan = 0;
                if (int.TryParse(td.Attributes["colspan"], CultureInfo.InvariantCulture, out var cs))
                {
                    colspan = cs;
                }
                columnsInThisRow += Math.Max(colspan, 1);
            }
            columns = Math.Max(columns, columnsInThisRow);
        }
        return (rows, columns);
    }

    /* convert images and figures that have properties like data-src into images that can be loaded without JS */
    private static void FixLazyImages(ParentTag articleContent)
    {
        foreach (var elem in articleContent.FindAll<Tag>(t => t is { Name: "img" or "picture" or "figure" }))
        {
            // In some sites (e.g. Kotaku), they put 1px square image as base64 data uri in the src attribute.
            // So, here we check if the data uri is too short, just might as well remove it.
            var elemSrc = elem.Attributes["src"];
            if (!elemSrc.IsEmpty && DataUrl.TryParse(elemSrc, out var dataUrl))
            {
                // Make sure it's not SVG, because SVG can have a meaningful image in under 133 bytes.
                if (elemSrc[dataUrl.MimeTypeRange].Equals(@"image/svg+xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Make sure this element has other attributes which contains image.
                // If it doesn't, then this src is important and shouldn't be removed.
                var srcCouldBeRemoved = false;
                foreach (var attr in elem.EnumerateAttributes())
                {
                    if (attr.Name == "src")
                    {
                        continue;
                    }

                    if (attr.ValueHasImage())
                    {
                        srcCouldBeRemoved = true;
                        break;
                    }
                }

                // Here we assume if image is less than 100 bytes (or 133B after encoded to base64)
                // it will be too small, therefore it might be placeholder image.
                if (srcCouldBeRemoved)
                {
                    var b64length = dataUrl.DataRange.GetOffsetAndLength(elemSrc.Length).Length;
                    if (b64length < 133)
                    {
                        elem.TryRemoveAttribute("src");
                    }
                }
            }

            // also check for "null" to work around https://github.com/jsdom/jsdom/issues/2580
            if ((elem.Attributes.Has("src") || (elem.Attributes.Has("srcset") && !elem.Attributes.Has("srcset", "null"))) &&
                !elem.Attributes.Has("class", "lazy"))
            {
                continue;
            }

            foreach (var attr in elem.EnumerateAttributes())
            {
                if (attr is { Name: "src" or "srcset" or "alt" })
                {
                    continue;
                }

                var copyTo = attr.ValueHasImageWithSize() ? "srcset" : attr.ValueHasImage() ? "src" : null;
                if (copyTo is { Length: > 0 })
                {
                    //if this is an img or picture, set the attribute directly
                    if (elem is { Name: "img" or "picture" })
                    {
                        elem.Attributes[copyTo] = attr.Value;
                    }
                    else if (elem is ParentTag { Name: "figure" } figure && !figure.FindAll<Tag>(t => t is { Name: "img" or "picture" }).Any())
                    {
                        //if the item is a <figure> that does not contain an image or picture, create one and place it inside the figure
                        //see the nytimes-3 testcase for an example
                        var img = Document.Html.CreateTag("img");
                        img.Attributes[copyTo] = attr.Value;
                        figure.Add(img);
                    }
                }
            }
        }
    }

    /**
     * Clean an element of all tags of type "tag" if they look fishy.
     * "Fishy" is an algorithm based on content length, classnames, link density, number of images & embeds, etc.
     *
     * @return void
     **/
    private void CleanConditionally(ParentTag articleContent, string tagName)
    {
        // First check if this node IS data table, in which case don't remove it.
        static bool IsDataTable(ParentTag t)
        {
            return t.Attributes.Has("_readabilityDataTable", "true");
        }

        if (!this.flags.HasFlag(CleanFlags.CleanConditionally))
            return;

        // Gather counts for other typical elements embedded within.
        // Traverse backwards so we can remove nodes at the same time
        // without effecting the traversal.
        //
        // TODO: Consider taking into account original contentScore here.
        var isList = tagName == "ul" || tagName == "ol";
        foreach (var node in articleContent.FindAll<ParentTag>(tag => tag.Name == tagName).ToArray())
        {
            if (!isList)
            {
                var listLength = 0d;
                var listNodes = node.FindAll<ParentTag>(t => t is { Name: "ul" or "ol" });
                foreach (var list in listNodes)
                {
                    listLength += list.GetContentLength();
                }
                isList = listLength / node.GetContentLength() > 0.9;
            }

            if (tagName == "table" && IsDataTable(node))
            {
                continue;
            }

            // Next check if we're inside a data table, in which case don't remove it as well.
            if (node.EnumerateAncestors().Any(p => p.Name == "table" && IsDataTable(p)))
            {
                continue;
            }

            if (node.EnumerateAncestors().Any(t => t.Name == "code"))
            {
                continue;
            }

            var weight = GetClassWeight(node);
            Debug.WriteLine($"Cleaning Conditionally \n{node.ToText()}\n");
            var contentScore = 0;
            if (weight + contentScore < 0)
            {
                node.Remove();
                continue;
            }

            if (node.GetCharCount(Commas) < 10)
            {
                // If there are not very many commas, and the number of
                // non-paragraph elements is more than paragraphs or other
                // ominous signs, remove the element.
                var p = node.FindAll<ParentTag>(t => t.Name == "p").Count();
                var img = node.FindAll<Tag>(t => t.Name == "img").Count();
                var li = node.FindAll<ParentTag>(t => t.Name == "li").Count() - 100;
                var input = node.FindAll<Tag>(t => t.Name == "input").Count();
                var headingDensity = GetTextDensity(node, t => t.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6");

                var embedCount = 0;
                var embeds = node.FindAll<Tag>(t => t.Name is "object" or "embed" or "iframe");
                var hasVideoEmbed = false;
                foreach (var embed in embeds)
                {
                    // If this embed has attribute that matches video regex, don't delete it.
                    if (IsVideoEmbed(embed))
                    {
                        hasVideoEmbed = true;
                    }
                    embedCount++;
                }
                if (hasVideoEmbed) continue;

                var linkDensity = GetLinkDensity(node);
                var contentLength = node.GetContentLength();

                var haveToRemove =
                  (img > 1 && p / img < 0.5 && !node.EnumerateAncestors().Any(a => a.Name == "figure")) ||
                  (!isList && li > p) ||
                  (input > Math.Floor((double)p / 3)) ||
                  (!isList && headingDensity < 0.9 && contentLength < 25 && (img == 0 || img > 2) && !node.EnumerateAncestors().Any(a => a.Name == "figure")) ||
                  (!isList && weight < 25 && linkDensity > 0.2) ||
                  (weight >= 25 && linkDensity > 0.5) ||
                  ((embedCount == 1 && contentLength < 75) || embedCount > 1);
                // Allow simple lists of images to remain in pages
                if (isList && haveToRemove)
                {
                    // Don't filter in lists with li's that contain more than one child
                    if (node.Any(e => e is ParentTag p && p.Count(e => e is Tag) > 1))
                    {
                        if (haveToRemove)
                        {
                            node.Remove();
                        }
                        else
                        {
                            continue;
                        }
                    }

                    var li_count = node.FindAll<ParentTag>(t => t.Name == "li").Count();
                    // Only allow the list to remain if every li contains an image
                    if (img == li_count)
                    {
                        continue;
                    }
                }
                if (haveToRemove)
                {
                    node.TryRemove();
                }
                else
                {
                    continue;
                }
            }
        }
    }

    private static float GetTextDensity(ParentTag e, Func<Tag, bool> selector)
    {
        var textLength = e.GetContentLength(true);
        if (textLength == 0) return 0f;

        var children = e.FindAll(selector);
        var childrenLength = children.Sum(child => child.GetContentLength(true));
        return (float)childrenLength / textLength;
    }

    /**
     * Clean a node of all elements of type "tag".
     * (Unless it's a youtube/vimeo video. People love movies.)
     *
     * @param Element
     * @param string tagName to clean
     * @return void
     **/
    private static void Clean(ParentTag articleContent, string tagName)
    {
        foreach (var element in articleContent.FindAll<Tag>(tag => tag.Name == tagName).ToArray())
        {
            // Allow youtube and vimeo videos through as people usually want to see those.
            if (IsVideoEmbed(element))
            {
                continue;
            }

            element.Remove();
        }
    }

    private static bool IsVideoEmbed(Tag element)
    {
        if (element.Name is not "object" and not "embed" and not "iframe")
            return false;

        // First, check the elements attributes to see if any of them contain youtube or vimeo
        if (element.Attributes.Any(attr => AllowedVideoHosts.Any(vh => attr.Value.Contains(vh, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        // For embed with <object> tag, check inner HTML as well.
        if (element is ParentTag { Name: "object" } obj &&
            obj.Any(el =>
                el is Tag tag && tag.Attributes.Any(attr =>
                    AllowedVideoHosts.Any(vh => attr.Value.Contains(vh, StringComparison.OrdinalIgnoreCase)))))
        {
            return true;
        }

        return false;
    }

    /**
     * Run any post-process modifications to article content as necessary.
     *
     * @param Element
     * @return void
    **/
    // _postProcessContent
    private void PostProcessContent(ParentTag articleContent)
    {
        // Readability cannot open relative uris so we convert them to absolute uris.
        FixRelativeUris(articleContent);

        SimplifyNestedElements(articleContent);

        if (!this.keepClasses)
        {
            // Remove classes.
            CleanClasses(articleContent);
        }
    }

    /**
     * Converts each <a> and <img> uri in the given element to an absolute URI,
     * ignoring #ref URIs.
     *
     * @param Element
     * @return void
     */
    // _fixRelativeUris
    private void FixRelativeUris(ParentTag articleContent)
    {
        foreach (var link in articleContent.FindAll<ParentTag>(e => e.Name == "a").ToArray())
        {
            var href = link.Attributes["href"];
            if (!href.IsEmpty)
            {
                // Remove links with javascript: URIs, since
                // they won't work after scripts have been removed from the page.
                if (href.StartsWith("javascript:"))
                {
                    // if the link only contains simple text content, it can be converted to a text node
                    if (link.Count() == 1 && link.Single() is Content textContent)
                    {
                        var text = textContent.Clone();
                        link.ReplaceWith(text);
                    }
                    else
                    {
                        // if the link has multiple children, they should all be preserved
                        var container = (ParentTag)Document.Html.CreateTag("span");
                        while (link.FirstOrDefault() is Element child)
                        {
                            link.Remove(child);
                            container.Add(child);
                        }
                        link.ReplaceWith(container);
                    }
                }
                else if (this.documentUrl.TryMakeAbsolute(href, out var absoluteUrl))
                {
                    link.Attributes["href"] = absoluteUrl;
                }
            }
        }

        var medias = articleContent.FindAll<Tag>(e => e.Name is "img" or "picture" or "figure" or "video" or "audio" or "source");
        foreach (var media in medias)
        {
            if (media.Attributes["src"] is { Length: > 0 } src && this.documentUrl.TryMakeAbsolute(src, out var absoluteSrc))
            {
                media.Attributes["src"] = absoluteSrc;
            }
            if (media.Attributes["poster"] is { Length: > 0 } poster && this.documentUrl.TryMakeAbsolute(poster, out var absolutePoster))
            {
                media.Attributes["poster"] = absolutePoster;
            }
            if (media.Attributes["srcset"] is { Length: > 0 } srcset)
            {
                var absoluteSrcset = new StringBuilder();

                var sets = srcset.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var set in sets)
                {
                    var span = set.AsSpan();
                    var urlEnd = span.IndexOf(' ');
                    if (urlEnd > 0)
                    {
                        var url = span[..urlEnd];
                        var descStart = span[urlEnd..].IndexOfAnyExcept(' ');
                        if (descStart > 0)
                        {
                            descStart += urlEnd;
                            var desc = span[descStart..];

                            if (absoluteSrcset.Length > 0) absoluteSrcset.Append(", ");
                            if (this.documentUrl.TryMakeAbsolute(url, out var absoluteUrl))
                            {
                                absoluteSrcset.Append(absoluteUrl);
                            }
                            else
                            {
                                absoluteSrcset.Append(url);
                            }
                            absoluteSrcset.Append(' ').Append(desc);
                        }
                    }
                }

                if (absoluteSrcset.Length > 0)
                {
                    media.Attributes["srcset"] = absoluteSrcset.ToString();
                }
            }
        }
    }

    private static void SimplifyNestedElements(ParentTag articleContent)
    {
        Tag? node = articleContent;

        while (node is not null)
        {
            if (node.Parent is not null && node.Name is "div" or "section" &&
                !node.Attributes["id"].StartsWith("readability", StringComparison.OrdinalIgnoreCase))
            {
                if (IsElementWithoutContent(node))
                {
                    node = node.RemoveAndGetNextTag();
                    continue;
                }
                else if (node is ParentTag parent &&
                    (HasSingleTagInsideElement(parent, "div") ||
                    HasSingleTagInsideElement(parent, "section")))
                {
                    var child = parent.First<ParentTag>();
                    foreach (var attr in parent.EnumerateAttributes())
                    {
                        child.Attributes[attr.Name] = attr.Value;
                    }
                    parent.Remove(child);
                    node.ReplaceWith(child);

                    node = child;
                    continue;
                }
            }

            node = node.NextTagOrDefault();
        }
    }

    /**
     * Removes the class="" attribute from every element in the given
     * subtree, except those that match CLASSES_TO_PRESERVE and
     * the classesToPreserve array from the options object.
     *
     * @param Element
     * @return void
     */
    private void CleanClasses(ParentTag articleContent)
    {
        var className = new StringBuilder();
        foreach (var node in articleContent.FindAll<Tag>(t => t.Attributes.Has("class")))
        {
            className.Clear();
            foreach (var klass in node.Attributes["class"].EnumerateValues())
            {
                foreach (var reserved in this.classesToPreserve)
                {
                    if (klass.Equals(reserved, StringComparison.OrdinalIgnoreCase))
                    {
                        if (className.Length > 0) className.Append(' ');
                        className.Append(klass);
                        break;
                    }
                }
            }
            if (className.Length > 0)
            {
                node.Attributes["class"] = className.ToString();
            }
            else
            {
                node.TryRemoveAttribute("class");
            }
        }
    }

    private static string? GetArticleExcerpt(ParentTag articleContent)
    {
        // If we haven't found an excerpt in the article's metadata, use the article's
        // first paragraph as the excerpt. This is used for displaying a preview of
        // the article's content.
        if (articleContent.Find<ParentTag>(e => e.Name == "p") is ParentTag para &&
            para.FirstOrDefault() is Content textContent)
        {
            return textContent.Data.ToTrimString();
        }

        return null;
    }

    private float GetClassWeight(Tag tag)
    {
        if (!this.flags.HasFlag(CleanFlags.WeightClasses))
            return 0f;

        var weight = 0f;

        //todo: we enumerate class names here. should we do it at all?
        foreach (var className in tag.Attributes["class"].EnumerateValues())
        {
            weight += GetNameWeight(className);
        }

        var id = tag.Attributes["id"];
        if (!id.IsEmpty)
        {
            weight += GetNameWeight(id);
        }

        return weight;

        static float GetNameWeight(ReadOnlySpan<char> name)
        {
            var weight = 0f;
            foreach (var negativeName in NegativeNames)
            {
                if (name.Contains(negativeName, StringComparison.OrdinalIgnoreCase))
                {
                    weight -= 25f;
                    break;
                }
            }
            foreach (var positiveName in PositiveNames)
            {
                if (name.Contains(positiveName, StringComparison.OrdinalIgnoreCase))
                {
                    weight += 25f;
                    break;
                }
            }
            return weight;
        }
    }

    private static float GetLinkDensity(ParentTag parent)
    {
        var textLength = parent.GetContentLength();
        if (textLength == 0)
            return 0f;

        var linkLength = 0f;
        foreach (var a in parent.FindAll(t => t is ParentTag { Name: "a" }).Cast<ParentTag>())
        {
            var href = a.Attributes["href"].Trim();
            var coefficient = href.Length > 1 && href[0] == '#' ? 0.3f : 1f;

            linkLength += a.GetContentLength() * coefficient;
        }

        return linkLength / textLength;
    }

    private static string GetInnerText(Element element, bool normalizeSpaces = true)
    {
        var text = element.ToString()!.Trim();

        if (normalizeSpaces)
        {
            var normalized = new StringBuilder(text.Length);

            foreach (var token in text.EnumerateTokens())
            {
                if (token.Category == TokenCategory.WhiteSpace)
                    normalized.Append(' ');
                else
                    normalized.Append(token.Span);
            }

            text = normalized.ToString();
        }

        return text;
    }

    private static bool HasChildBlockElement(ParentTag parent)
    {
        return parent.Any(el => el is ParentTag tag && (DivToParaElems.Contains(tag.Name) || HasChildBlockElement(tag)));
    }

    private static bool HasSingleTagInsideElement(ParentTag parent, string tagName)
    {
        // There should be exactly 1 element child with given tag
        if (parent.Count(t => t is Tag tag && tag.Name == tagName) != 1)
            return false;

        // And there should be no text nodes with real content
        return !parent.Any(el => el is Content content && !content.Data.IsWhiteSpace());
    }

    private static bool IsWhiteSpace(Element element)
    {
        return element switch
        {
            CharacterData chars when chars is not Comment => chars.Data.IsWhiteSpace(),
            Tag { Name: "br" } => true,
            _ => false
        };
    }

    private static bool IsElementWithoutContent(Element node)
    {
        if (node is not ParentTag parent)
            return true;
        if (!parent.Any())
            return true;

        var text = parent.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return true;

        return parent.All(el => el is Tag { Name: "br" or "hr" } || (el is Content content && content.Data.IsWhiteSpace()));
    }

    private bool HeaderDuplicatesTitle(Tag tag)
    {
        if (tag is not ParentTag { Name: "h1" or "h2" } header)
            return false;

        var heading = header.ToString();
        Debug.WriteLine($"Evaluating similarity of header: '{heading}', '{this.articleTitle}'");
        return ComparisonMethods.JaroWinklerSimilarity(this.articleTitle, heading) > 0.75f;
    }

    private bool CheckByline(Tag tag, string matchString)
    {
        if (this.articleByline is not null)
            return false;

        var textContent = tag.ToString().Trim();
        if ((tag.Attributes.Has("rel", "author") || tag.Attributes.Has("itemprop", "author") ||
            Bylines.Any(bl => matchString.Contains(bl, StringComparison.OrdinalIgnoreCase))) &&
            IsValidByline(textContent))
        {
            this.articleByline = textContent;
            return true;
        }

        return false;
    }

    /**
     * Check whether the input string could be a byline.
     * This verifies that the input is a string, and that the length
     * is less than 100 chars.
     *
     * @param possibleByline {string} - a string to check whether its a byline.
     * @return Boolean - whether the input string is a byline.
     */
    // _isValidByline
    private static bool IsValidByline(ReadOnlySpan<char> byline)
    {
        return byline.Length > 0 && byline.Length < 100;
    }

    private static bool IsProbablyVisible(Tag tag)
    {
        if (!tag.HasAttributes)
            return true;

        if (tag.Attributes.Has("style", "hidden"))
        {
            //todo: parse styles?
            return false;
        }

        if (tag.Attributes.Has("hidden"))
            return false;
        if (tag.Attributes.Has("aria-hidden", "true"))
            return false;

        return true;
    }
}
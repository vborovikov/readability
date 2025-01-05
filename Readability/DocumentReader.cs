namespace Readability;

using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    private const int DefaultNTopCandidates = 5;
    internal const int DefaultCharThreshold = 500;

    private static readonly string[] DefaultClassesToPreserve = ["caption"];

    public static readonly ReadabilityOptions Default = new();

    /// <summary>
    /// The number of top candidates to consider when analysing how
    /// tight the competition is among candidates. 
    /// </summary>
    public int NTopCandidates { get; init; } = DefaultNTopCandidates;

    /// <summary>
    /// The number of chars an article must have in order to return a result.
    /// </summary>
    public int CharThreshold { get; init; } = DefaultCharThreshold;
    public string[] ClassesToPreserve { get; init; } = DefaultClassesToPreserve;
    public bool KeepClasses { get; init; }
}

public partial class DocumentReader
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

    private static readonly string[] Bylines = ["byline", "author", "dateline", "writtenby", "p-author"];

    private static readonly string[] DivToParaElems = ["blockquote", "dl", "div", "img", "ol", "p", "pre", "table", "ul"];

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
    private readonly bool keepClasses;
    private readonly string[] classesToPreserve;

    public DocumentReader(Document document)
        : this(document, ReadabilityOptions.Default) { }

    public DocumentReader(Document document, Uri documentUri)
        : this(document, documentUri, ReadabilityOptions.Default) { }

    public DocumentReader(Document document, ReadabilityOptions options)
        : this(document, new DocumentUrl(document), options) { }

    public DocumentReader(Document document, Uri documentUri, ReadabilityOptions options)
        : this(document, new DocumentUrl(documentUri, document), options) { }

    internal DocumentReader(Document document, DocumentUrl documentUrl, ReadabilityOptions options)
    {
        this.document = document.IsSerialized ? document : document.Clone();
        this.documentUrl = documentUrl;
        this.flags = CleanFlags.All;

        this.nbTopCandidates = options.NTopCandidates;
        this.keepClasses = options.KeepClasses;
        this.classesToPreserve = [.. DefaultClassesToPreserve, .. options.ClassesToPreserve];
    }

    public bool TryFind(ArticlePath articlePath, [MaybeNullWhen(false)] out Article article)
    {
        article = default;
        return false;
    }

    public Article Find(ArticlePath articlePath)
    {
        if (!TryFind(articlePath, out var article))
            throw new ArticleNotFoundException();

        return article;
    }

    public bool TryParse([MaybeNullWhen(false)] out Article article)
    {
        UnwrapNoscriptImages();
        var jsonMetadata = GetJsonLD();
        var metadata = GetArticleMetadata(jsonMetadata);
        this.articleTitle = metadata.Title;
        var articleContent = GetArticleContent();
        if (articleContent is null)
        {
            article = default;
            return false;
        }

        // ReadabilityJS content alterations
        RemoveScripts(articleContent);
        PrepareDocument(articleContent);
        PostProcessContent(articleContent);

        article = new Article
        {
            Title = this.articleTitle,
            Byline = metadata.Byline ?? this.articleByline,
            Excerpt = metadata.Excerpt?.ToTrimString() ?? GetArticleExcerpt(articleContent),
            Content = articleContent,
            Length = articleContent.Length,
            SiteName = metadata.SiteName,
            Published = metadata.Published,
            Language = this.articleLang,
            Dir = this.articleDir,
        };
        return true;
    }

    public Article Parse()
    {
        if (!TryParse(out var article))
            throw new ArticleNotFoundException();

        return article;
    }

    #region New algorithm

    private ParentTag? GetArticleContent()
    {
        var html = this.document.FirstOrDefault<ParentTag>(h => h.Name == "html");
        if (html is not null && html.Attributes["lang"] is { Length: > 0 } lang)
        {
            this.articleLang = lang.ToString();
        }

        // find candidates with highest scores

        var candidates = new Dictionary<ParentTag, ArticleCandidate>();
        var contentScores = new PriorityQueue<ArticleCandidate, float>(this.nbTopCandidates);
        var body = html?.FirstOrDefault<ParentTag>(b => b.Name == "body") ?? (IRoot)this.document;
        foreach (var root in body.FindAll<ParentTag>())
        {
            CheckByline(root);

            if (root is { Layout: FlowLayout.Block, HasChildren: true } &&
                ArticleCandidate.TryCreate(root, out var candidate))
            {
                candidates.Add(root, candidate);

                if (contentScores.Count < nbTopCandidates)
                {
                    contentScores.Enqueue(candidate, candidate.ContentScore);

                }
                else
                {
                    contentScores.EnqueueDequeue(candidate, candidate.ContentScore);
                }
            }
        }

        // check ancestors of the top candidates
        Debug.WriteLine("");

        var ancestryCount = 0;
        var maxAncestryCount = 0;
        var articleCandidate = default(ArticleCandidate);
        var topCandidates = new SortedList<ArticleCandidate, ParentTag>(ArticleCandidate.ConstentScoreComparer);
        var commonAncestors = new Dictionary<ParentTag, int>(nbTopCandidates);
        while (contentScores.TryDequeue(out var candidate, out var score))
        {
            Debug.WriteLine($"{candidate.Path}: {score:F2} ({candidate.TokenCount}) [{candidate.NestingLevel}]");

            for (var parent = candidate.Root.Parent; parent is not null && parent != body; parent = parent.Parent)
            {
                ref var reoccurence = ref CollectionsMarshal.GetValueRefOrAddDefault(commonAncestors, parent, out _);
                ++reoccurence;
            }

            topCandidates.Add(candidate, candidate.Root);
            if (candidate.Root.Parent == articleCandidate.Root)
            {
                ++ancestryCount;
                if (ancestryCount > maxAncestryCount)
                {
                    maxAncestryCount = ancestryCount;
                }
            }
            else
            {
                ancestryCount = 0;
            }

            articleCandidate = candidate;
        }

        Debug.WriteLine($"ancestry: {ancestryCount} max-ancestry: {maxAncestryCount}");

        var topmostCandidate = topCandidates.First().Value;
        var ancestryThreshold = nbTopCandidates / 2 + nbTopCandidates % 2; // 3 occurrences in case of 5 candidates
        if (maxAncestryCount / (float)ancestryThreshold < 0.6f &&
            (ancestryCount == 0 || ancestryCount != maxAncestryCount))
        {
            // the top candidates are mostly unrelated, check their common ancestors

            var foundRelevantAncestor = false;
            var midTokenCount = GetMedianTokenCount(topCandidates.Keys);
            var maxTokenCount = topCandidates.Max(ca => ca.Key.TokenCount);
            foreach (var (ancestor, reoccurrence) in commonAncestors.OrderBy(ca => ca.Value).ThenByDescending(ca => ca.Key.NestingLevel))
            {
                if (!candidates.TryGetValue(ancestor, out var ancestorCandidate))
                    continue;

                Debug.WriteLine($"{ancestor.GetPath()}: {reoccurrence} {ancestorCandidate.ContentScore:F2} ({ancestorCandidate.TokenCount}) [{ancestorCandidate.NestingLevel}]");

                if (!foundRelevantAncestor && (
                    (reoccurrence == nbTopCandidates && !topCandidates.ContainsValue(ancestor)) ||
                    (reoccurrence > ancestryThreshold && ancestorCandidate.TokenCount > maxTokenCount) ||
                    (reoccurrence == ancestryThreshold && (topCandidates.ContainsValue(ancestor) && maxAncestryCount > 0 || ancestor == topmostCandidate)) ||
                    (reoccurrence < ancestryThreshold && ancestor == topmostCandidate && ancestorCandidate.TokenCount >= midTokenCount)) &&
                    ancestorCandidate.TokenCount >= articleCandidate.TokenCount)
                {
                    // the ancestor candidate must have at least the same number of tokens as previous candidate
                    articleCandidate = ancestorCandidate;
                    foundRelevantAncestor = true;

                    //todo: DocumentReader break here
                }
            }
        }
        else if (ArticleCandidate.HasOutlier(candidates.Values, out var outlier))
        {
            // the outlier candidate has much more content
            articleCandidate = outlier;
        }
        else if (ancestryCount / (float)ancestryThreshold > 0.6f)
        {
            // too many parents, find the first grandparent amoung the top candidates
            var grandparent = topCandidates.Keys[ancestryCount];
            var ratio = articleCandidate.TokenCount / (float)grandparent.TokenCount;
            if (ratio <= 0.8f)
            {
                // the grandparent candidate has significantly more content
                articleCandidate = grandparent;
            }
        }
        else if (topCandidates.Count(ca => ca.Key.NestingLevel == topmostCandidate.NestingLevel) > 1)
        {
            // some top candidates have the same nesting level,
            // choose their common ancestor if it's also a top candidate

            var sameLevelCandidates = topCandidates
                .Where(ca => ca.Key.NestingLevel == topmostCandidate.NestingLevel)
                .ToArray();

            foreach (var ancestor in topCandidates.IntersectBy(commonAncestors.Keys, ca => ca.Value))
            {
                if (sameLevelCandidates.All(ca => ancestor.Value.Find<ParentTag>(rt => rt == ca.Value) is not null))
                {
                    articleCandidate = ancestor.Key;
                    break;
                }
            }
        }

        if (articleCandidate == default)
            return null;

        Debug.WriteLine($"\nArticle: {articleCandidate.Path} {articleCandidate.ContentScore:F2} ({articleCandidate.TokenCount})");
        var articleContent = articleCandidate.Root;

        // backward compatibility with ReadabilityJS
        if (articleContent is not { Name: "article" or "section" or "div" or "main" })
        {
            var articleRoot = (ParentTag)Document.Html.CreateTag("div");
            articleContent.Remove();
            articleRoot.Add(articleContent);
            articleContent = articleRoot;
        }

        articleContent.Attributes["id"] = "readability-page-1";
        articleContent.Attributes["class"] = "page";

        // So we have all of the content that we need. Now we clean it up for presentation.
        PrepArticle(articleContent);

        return articleContent;
    }

    private static int GetMedianTokenCount(IEnumerable<ArticleCandidate> topCandidates)
    {
        var candidates = topCandidates
            .Order(ArticleCandidate.TokenCountComparer)
            .ToArray();

        var count = candidates.Length;
        var mid = count / 2;

        if (count % 2 != 0)
            return candidates[mid].TokenCount;

        return (candidates[mid - 1].TokenCount + candidates[mid].TokenCount) / 2;
    }

    #endregion New algorithm

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
        foreach (var jsonld in this.document.FindAll<ParentTag>(t => t.Name == "script" && t.Attributes.Has("type", "application/ld+json")))
        {
            try
            {
                var content = jsonld.FirstOrDefault<CharacterData>();
                if (content is null)
                    continue;

                var parsed = JsonDocument.Parse(content.Data.Trim().Trim(';').Trim().ToString()).RootElement;
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

                    metadata.Title = (headlineMatches && !nameMatches ? parsedHeadline : parsedName).ToTrimString();
                }
                else if (parsed.TryGetString("name", out var parsedNameOnly))
                {
                    metadata.Title = parsedNameOnly.ToTrimString();
                }
                else if (parsed.TryGetString("headline", out var parsedHeadlineOnly))
                {
                    metadata.Title = parsedHeadlineOnly.ToTrimString();
                }

                if (parsed.TryGetProperty("author", out var authorProp))
                {
                    if (authorProp.TryGetString("name", out var oneAuthorName))
                    {
                        metadata.Byline = oneAuthorName.ToTrimString();
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

                                authors.Append(authorName.ToTrimString());
                            }
                        }

                        metadata.Byline = authors.ToString();
                    }
                }

                if (parsed.TryGetString("description", out var parsedDescription))
                {
                    metadata.Excerpt = parsedDescription.ToTrimString();
                }
                else if (parsed.TryGetString("summary", out var parsedSummary))
                {
                    metadata.Excerpt = parsedSummary.ToTrimString();
                }

                if (parsed.TryGetProperty("publisher", out var publisherProp) &&
                    publisherProp.TryGetString("name", out var publisherName))
                {
                    metadata.SiteName = publisherName.ToTrimString();
                }
                else if (parsed.TryGetProperty("creator", out var creatorProp) &&
                    creatorProp.TryGetString("name", out var creatorName))
                {
                    metadata.SiteName = creatorName.ToTrimString();
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
            catch (Exception x)
            {
                Debug.WriteLine($"GetJsonLD: {x.Message}");
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
        var separatorIndex = titleTokens.LastIndexOf(3, tks =>
        {
            if (tks[0].Category != TokenCategory.WhiteSpace || tks[^1].Category != TokenCategory.WhiteSpace)
                return false;

            return tks[1].Span.Length == 1 && HierarchicalSeparators.Contains(tks[1].Span[0]);
        });

        // If there's a separator in the title, first remove the final part
        var titleHadHierarchicalSeparators = separatorIndex > 0;
        if (titleHadHierarchicalSeparators)
        {
            var beforeTokens = titleTokens[..separatorIndex];
            var beforeWordCount = CountWords(beforeTokens);
            var afterTokens = titleTokens[(separatorIndex + 3)..];
            var afterWordCount = CountWords(afterTokens);

            // If the resulting title is too short (3 words or fewer), remove
            // the first part instead:
            curTitle = beforeWordCount > afterWordCount || beforeWordCount >= 3 ? beforeTokens : afterTokens;
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

        // If we now have 4 words or fewer as our title, and either no
        // 'hierarchical' separators (\, /, > or ») were found in the original
        // title or we decreased the number of words by more than 1 word, use
        // the original title.
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

    private static void RemoveScripts(ParentTag root)
    {
        foreach (var scriptTag in root.FindAll<Tag>(t => t.Name == "script" || t.Name == "noscript").ToArray())
        {
            scriptTag.Remove();
        }
    }

    private static void PrepareDocument(ParentTag root)
    {
        // remove comments
        foreach (var comment in root.FindAll(e => e is Comment).ToArray())
        {
            comment.Remove();
        }

        // Remove all style tags in head
        foreach (var styleTag in root.FindAll<Tag>(t => t.Name == "style").ToArray())
        {
            styleTag.Remove();
        }

        if (root.Find<ParentTag>(el => el.Name == "body") is ParentTag body)
        {
            ReplaceBrs(body);
            ReplaceTags(body, "font", "span");
        }
    }

    private static void ReplaceBrs(ParentTag elem)
    {
        var brs = elem.FindAll<Tag>(t => t.Name == "br").ToArray();
        foreach (var br in brs)
        {
            if (br.Parent is null)
                continue;

            var next = br.NextSiblingOrDefault();

            // Whether 2 or more <br> elements have been found and replaced with a
            // <p> block.
            var replaced = false;

            // If we find a <br> chain, remove the <br>s until we hit another node
            // or non-whitespace. This leaves behind the first <br> in the chain
            // (which will be replaced with a <p> later).
            while ((next = next.NextElementOrDefault()) is Tag { Name: "br" })
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

                next = p.NextSiblingOrDefault();
                while (next is not null)
                {
                    // If we've hit another <br><br>, we're done adding children to this <p>.
                    if (next is Tag { Name: "br" })
                    {
                        var nextElem = next.NextSiblingOrDefault()?.NextElementOrDefault();
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

                while (p.LastOrDefault() is Element element && IsWhiteSpace(element))
                {
                    p.Remove(element);
                }

                if (p.Parent?.Name == "p")
                {
                    ChangeTagName(p.Parent, "div");
                }
            }
        }
    }

    /***
     * Determine if a node qualifies as phrasing content.
     * https://developer.mozilla.org/en-US/docs/Web/Guide/HTML/Content_categories#Phrasing_content
    **/
    // _isPhrasingContent
    private static bool IsPhrasingContent(Element element)
    {
        return element switch
        {
            CharacterData chars when chars is not Comment => true,
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
        Debug.WriteLine($"_setNodeTag {tag.ToIdString()} '{newName}'");

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
            var content = meta.Attributes["content"];
            if (content.IsEmpty)
                continue;

            var property = meta.Attributes["property"];
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
                var name = meta.Attributes["name"];
                if (!name.IsEmpty)
                {
                    values[ToCleanString(name)] = content.ToString();
                }
            }
        }

        return new ArticleMetadata
        {
            Title = jsonMetadata?.Title ?? WebUtility.HtmlDecode(FindVerboseString(
                values.GetValueOrDefault("dc:title"),
                values.GetValueOrDefault("dcterm:title"),
                values.GetValueOrDefault("og:title"),
                values.GetValueOrDefault("weibo:article:title"),
                values.GetValueOrDefault("weibo:webpage:title"),
                values.GetValueOrDefault("title"),
                values.GetValueOrDefault("twitter:title")) ??
                GetArticleTitle()),

            Byline = jsonMetadata?.Byline ?? WebUtility.HtmlDecode(FindVerboseString(
                values.GetValueOrDefault("dc:creator"),
                values.GetValueOrDefault("dcterm:creator"),
                values.GetValueOrDefault("author"))),

            Excerpt = jsonMetadata?.Excerpt ?? WebUtility.HtmlDecode(FindVerboseString(
                values.GetValueOrDefault("dc:description"),
                values.GetValueOrDefault("dcterm:description"),
                values.GetValueOrDefault("og:description"),
                values.GetValueOrDefault("weibo:article:description"),
                values.GetValueOrDefault("weibo:webpage:description"),
                values.GetValueOrDefault("description"),
                values.GetValueOrDefault("twitter:description"))),

            SiteName = jsonMetadata?.SiteName ?? WebUtility.HtmlDecode(
                values.GetValueOrDefault("og:site_name")),

            Published = jsonMetadata?.Published ?? (DateTimeOffset.TryParse(WebUtility.HtmlDecode(
                values.GetValueOrDefault("article:published_time") ??
                values.GetValueOrDefault("parsely-pud-date") ??
                values.GetValueOrDefault("article:modified_time")), out var dateTime) ? dateTime : null),
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

        static string? FindVerboseString(params string?[] strings)
        {
            string? verboseString = null;
            var wordCount = 0;

            foreach (var str in strings)
            {
                if (str is not null)
                {
                    var strWordCount = CountWords(str);
                    if (verboseString is null)
                    {
                        verboseString = str;
                        wordCount = strWordCount;
                    }
                    else
                    {
                        if (strWordCount > wordCount ||
                            (strWordCount == wordCount && str.Length > verboseString.Length))
                        {
                            verboseString = str;
                            wordCount = strWordCount;
                        }
                    }
                }
            }

            return verboseString;
        }

        static int CountWords(string str)
        {
            var count = 0;
            foreach (var token in str.EnumerateTokens())
            {
                if (token.Category == TokenCategory.Word)
                    ++count;
            }
            return count;
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

        var shareElementThreshold = ReadabilityOptions.DefaultCharThreshold;
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
            var imgCount = paragraph.FindAll(t => t is Tag { Name: "img" }).Count();
            var embedCount = paragraph.FindAll(t => t is Tag { Name: "embed" }).Count();
            var objectCount = paragraph.FindAll(t => t is Tag { Name: "object" }).Count();
            // At this point, nasty iframes have been removed, only remain embedded video ones.
            var iframeCount = paragraph.FindAll(t => t is Tag { Name: "iframe" }).Count();
            var totalCount = imgCount + embedCount + objectCount + iframeCount;

            if (totalCount == 0 && GetInnerText(paragraph, false).Length == 0)
            {
                paragraph.Remove();
            }
        }

        foreach (var br in articleContent.FindAll<Tag>(t => t.Name == "br").ToArray())
        {
            var next = br.NextSiblingOrDefault().NextElementOrDefault();
            if (next is ParentTag { Name: "p" })
            {
                br.Remove();
            }
        }

        // Remove single-cell tables
        foreach (var table in articleContent.FindAll<ParentTag>(t => t.Name == "table").ToArray())
        {
            var tbody = table.HasSingleTagInside("tbody") ? table.First<ParentTag>() : table;
            if (tbody.HasSingleTagInside("tr"))
            {
                var row = tbody.First<ParentTag>();
                if (row.HasSingleTagInside("td"))
                {
                    var cell = row.First<ParentTag>();
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
                Debug.WriteLine($"Removing header with low class weight: {heading.Name}\n{heading.ToText()}\n");
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
            Debug.WriteLine($"Cleaning Conditionally {node.ToIdString()} with weight {weight}");
            var contentScore = 0;
            if (weight + contentScore < 0f)
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
                  (img > 1 && ((float)p / img) < 0.5f && !node.EnumerateAncestors().Any(a => a.Name == "figure")) ||
                  (!isList && li > p) ||
                  (input > Math.Floor(p / 3d)) ||
                  (!isList && headingDensity < 0.9f && contentLength < 25 && (img == 0 || img > 2) && !node.EnumerateAncestors().Any(a => a.Name == "figure")) ||
                  (!isList && weight < 25 && linkDensity > 0.2f) ||
                  (weight >= 25f && linkDensity > 0.5f) ||
                  ((embedCount == 1 && contentLength < 75) || embedCount > 1);

                Debug.WriteLine(
                    $"_cleanConditionally: link density: {linkDensity} content length: {contentLength} " +
                    $"p count: {p} img count: {img} li count: {li} input count {input} " +
                    $"heading density: {headingDensity} embed count: {embedCount} have to remove: {haveToRemove}");

                // Allow simple lists of images to remain in pages
                if (isList && haveToRemove)
                {
                    // Don't filter in lists with li's that contain more than one child
                    if (node.Any(e => e is ParentTag p && p.Count<Tag>() > 1))
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
            if (link.Attributes["href"] is { Length: > 0 } href)
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
                    (parent.HasSingleTagInside("div") ||
                    parent.HasSingleTagInside("section")))
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
            para.Any<Content>())
        {
            return para.ToTrimString();
        }

        return null;
    }

    /**
     * Get an elements class/id weight. Uses regular expressions to tell if this
     * element looks good or bad.
     *
     * @param Element
     * @return number (Integer)
    **/
    // _getClassWeight
    private float GetClassWeight(Tag tag)
    {
        if (!this.flags.HasFlag(CleanFlags.WeightClasses))
            return 0f;

        var weight = 0f;

        if (tag.Attributes["class"] is { Length: > 0 } klass && TryGetNameWeight(klass, out var classWeight))
        {
            weight += classWeight;
        }

        if (tag.Attributes["id"] is { Length: > 0 } id && TryGetNameWeight(id, out var idWeight))
        {
            weight += idWeight;
        }

        return weight;

        static bool TryGetNameWeight(ReadOnlySpan<char> names, out float weight)
        {
            weight = 0f;
            var found = false;

            foreach (var name in names.EnumerateValues())
            {
                foreach (var negativeName in NegativeNames)
                {
                    if (name.Contains(negativeName, StringComparison.OrdinalIgnoreCase))
                    {
                        weight -= 25f;
                        found = true;
                        goto Outside;
                    }
                }
            }

        Outside:
            foreach (var name in names.EnumerateValues())
            {
                foreach (var positiveName in PositiveNames)
                {
                    if (name.Contains(positiveName, StringComparison.OrdinalIgnoreCase))
                    {
                        weight += 25f;
                        found = true;
                        goto Finish;
                    }
                }
            }

        Finish:
            return found;
        }
    }

    /**
     * Get the density of links as a percentage of the content
     * This is the amount of text that is inside a link divided by the total text in the node.
     *
     * @param Element
     * @return number (float)
    **/
    // _getLinkDensity
    private static float GetLinkDensity(ParentTag parent)
    {
        var textLength = parent.GetContentLength();
        if (textLength == 0)
            return 0f;

        var linkLength = 0f;
        foreach (var a in parent.FindAll<ParentTag>(t => t.Name == "a"))
        {
            var href = a.Attributes["href"];
            var coefficient = href.Length > 1 && href[0] == '#' ? 0.3f : 1f;

            linkLength += a.GetContentLength() * coefficient;
        }

        return linkLength / textLength;
    }

    /**
     * Get the inner text of a node - cross browser compatibly.
     * This also strips out any excess whitespace to be found.
     *
     * @param Element
     * @param Boolean normalizeSpaces (default: true)
     * @return string
    **/
    // _getInnerText
    private static string GetInnerText(Element element, bool normalizeSpaces = true)
    {
        return normalizeSpaces ? element.ToTrimString() : WebUtility.HtmlDecode(element.ToString())!.Trim();
    }

    /**
     * Determine whether element has any children block level elements.
     *
     * @param Element
     */
    //_hasChildBlockElement
    private static bool HasChildBlockElement(ParentTag parent)
    {
        return parent.Any<ParentTag>(tag => DivToParaElems.Contains(tag.Name) || HasChildBlockElement(tag));
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
        if (node is not ParentTag root)
            return false;
        if (!root.Any())
            return true;

        var text = root.ToTrimString();
        if (!string.IsNullOrWhiteSpace(text))
            return false;

        var tagCount = root.Count<Tag>();
        return tagCount == 0 || tagCount == root.FindAll<Tag>(t => t is { Name: "br" or "hr" }).Count();
    }

    private bool HeaderDuplicatesTitle(Tag tag)
    {
        if (tag is not ParentTag { Name: "h1" or "h2" } header)
            return false;

        var heading = header.ToTrimString();
        Debug.WriteLine($"Evaluating similarity of header: '{heading}', '{this.articleTitle}'");
        return ComparisonMethods.JaroWinklerSimilarity(this.articleTitle, heading) > 0.75f;
    }

    private bool CheckByline(Tag tag)
    {
        if (this.articleByline is not null)
            return false;

        //todo: use Element.TryFormat instead of Element.ToString

        if ((tag.Attributes.Has("rel", "author") || tag.Attributes.Has("itemprop", "author") ||
            HasBylineAttr(tag.Attributes["class"]) || HasBylineAttr(tag.Attributes["id"])) &&
            IsValidByline(tag.ToString()))
        {
            this.articleByline = tag.ToTrimString();
            return true;
        }

        return false;

        /*
         * Check whether the input string could be a byline.
         * This verifies that the input is a string, and that the length
         * is less than 100 chars.
         *
         * @param possibleByline {string} - a string to check whether its a byline.
         * @return Boolean - whether the input string is a byline.
         */
        // _isValidByline
        static bool IsValidByline(ReadOnlySpan<char> byline)
        {
            byline = byline.Trim();
            return byline.Length > 0 && byline.Length < 100;
        }

        static bool HasBylineAttr(ReadOnlySpan<char> attrValue)
        {
            if (attrValue.Length > 0)
            {
                foreach (var bylineName in Bylines)
                {
                    if (attrValue.Contains(bylineName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }
    }

    private static bool IsProbablyVisible(Tag tag)
    {
        if (!tag.HasAttributes)
            return true;

        if (tag.Attributes["style"] is { Length: > 0 } style)
        {
            foreach (var cssDeclaration in style.EnumerateCssDeclarations())
            {
                if (cssDeclaration.Property is "display" && cssDeclaration.Value is "none")
                    return false;
                if (cssDeclaration.Property is "visibility" && cssDeclaration.Value is "hidden")
                    return false;
            }
        }

        if (tag.Attributes.Has("hidden"))
            return false;
        if (tag.Attributes.Has("aria-hidden", "true"))
            return false;

        return true;
    }
}
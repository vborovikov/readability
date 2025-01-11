namespace Readability;

using System;
using System.Buffers;
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

    // These are the classes that readability sets itself.
    private static readonly string[] DefaultClassesToPreserve = ["page"];

    private readonly Document document;
    private readonly DocumentUrl documentUrl;
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

        this.nbTopCandidates = options.NTopCandidates;
        this.keepClasses = options.KeepClasses;
        this.classesToPreserve = [.. DefaultClassesToPreserve, .. options.ClassesToPreserve];
    }

    public bool TryParse([MaybeNullWhen(false)] out Article article)
    {
        UnwrapNoscriptImages();
        var jsonMetadata = GetJsonLD();
        var metadata = GetArticleMetadata(jsonMetadata);
        this.articleTitle = metadata.Title;
        if (!TryFindArticle(out var articleCandidate))
        {
            article = default;
            return false;
        }

        // ReadabilityJS content alterations
        RemoveScripts(articleCandidate.Root);
        PrepareDocument(articleCandidate.Root);
        PostProcessContent(articleCandidate.Root);

        article = new Article
        {
            Title = this.articleTitle,
            Byline = metadata.Byline ?? this.articleByline,
            Excerpt = metadata.Excerpt?.ToTrimString() ?? GetArticleExcerpt(articleCandidate.Root),
            Content = articleCandidate.Root,
            Length = articleCandidate.Root.Length,
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

    private bool TryFindArticle([NotNullWhen(true)] out ArticleCandidate result)
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

        if (topCandidates.Count == 0)
        {
            result = default;
            return false;
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

        if (articleCandidate != default)
        {
            Debug.WriteLine($"\nArticle: {articleCandidate.Path} {articleCandidate.ContentScore:F2} ({articleCandidate.TokenCount})");
            result = articleCandidate.Elect();
            return true;
        }

        result = default;
        return false;
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
                if (parsed.ValueKind == JsonValueKind.Array && parsed.GetArrayLength() > 0)
                {
                    parsed = parsed.EnumerateArray().First();
                }
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
}
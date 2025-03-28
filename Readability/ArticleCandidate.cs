namespace Readability;

using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Brackets;
using FuzzyCompare.Text;
#if CLI
using Termly;
#endif

[DebuggerDisplay("{Path,nq}: {ContentScore} ({TokenCount})")]
readonly record struct ArticleCandidate : IComparable<ArticleCandidate>
{
    private const int DefaultCharThreshold = 500;

    private ArticleCandidate(ParentTag root, int tokenCount, float contentScore)
    {
        this.Root = root;
        this.TokenCount = tokenCount;
        this.ContentScore = contentScore;
    }

    public static readonly IComparer<ArticleCandidate> ConstentScoreComparer = new CandidateContentScoreComparer();
    public static readonly IComparer<ArticleCandidate> TokenCountComparer = new CandidateTokenCountComparer();

    public ParentTag Root { get; }
    public int TokenCount { get; }
    public float ContentScore { get; }

    public string Path => this.Root.GetPath();
    public int NestingLevel => this.Root.NestingLevel;

    public int CompareTo(ArticleCandidate other)
    {
        var tokenCountRatio = this.TokenCount / (float)other.TokenCount;
        var contentScoreRatio = this.ContentScore / other.ContentScore;

        if (tokenCountRatio < 0.8f || contentScoreRatio < 0.5f)
        {
            return -1;
        }

        if (tokenCountRatio > 0.8f && contentScoreRatio > 0.5f)
        {
            return 1;
        }

        return 0;
    }

    public static bool TryCreate(ParentTag root, IRoot documentRoot, [NotNullWhen(true)] out ArticleCandidate candidate)
    {
        if (TryCountTokens(root, out var tokenCount, out var tokenDensity))
        {
            var markupCount = CountMarkup(root);
            var elementFactor = GetElementFactor(root, documentRoot);
            if (tokenCount > markupCount && (markupCount > 0 || elementFactor > 1f))
            {
                var contentScore = tokenCount / (markupCount + MathF.Log2(tokenCount)) * tokenDensity * elementFactor * MathF.Log(root.NestingLevel);

                Debug.WriteLine($"{root.GetPath()}: tokens: {tokenCount}, markup: {markupCount}, density: {tokenDensity}, factor: {elementFactor}, score: {contentScore}");

                candidate = new ArticleCandidate(root, tokenCount, contentScore);
                return true;
            }
        }

        candidate = default;
        return false;
    }

    public static bool TryFind(IRoot document, int topCandidateCount, [NotNullWhen(true)] out ArticleCandidate result)
    {
        // locate the document root element

        var documentRoot = document;
        if (document is not ParentTag { Name: "html" })
        {
            documentRoot = document.Find<ParentTag>(p => p.Name == "html") ?? document;
        }

        // find candidates with highest scores

        var candidates = new Dictionary<ParentTag, ArticleCandidate>();
        var contentScores = new PriorityQueue<ArticleCandidate, float>(topCandidateCount);
        foreach (var root in documentRoot.FindAll<ParentTag>(p => p is { Layout: FlowLayout.Block, HasChildren: true }))
        {
            if (TryCreate(root, documentRoot, out var candidate))
            {
                candidates.Add(root, candidate);

                if (contentScores.Count < topCandidateCount)
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
        var topCandidates = new SortedList<ArticleCandidate, ParentTag>(ConstentScoreComparer);
        var commonAncestors = new Dictionary<ParentTag, int>(topCandidateCount);
        while (contentScores.TryDequeue(out var candidate, out var score))
        {
#if CLI
            Console.Out.PrintLine($"{candidate.Path:cyan}: {score:F2:magenta} ({candidate.TokenCount}) [{candidate.NestingLevel:yellow}]");
#endif
            Debug.WriteLine($"{candidate.Path}: {score:F2} ({candidate.TokenCount}) [{candidate.NestingLevel}]");

            for (var parent = candidate.Root.Parent; parent is not null && parent != documentRoot; parent = parent.Parent)
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
        var ancestryThreshold = (topCandidateCount / 2) + (topCandidateCount % 2); // 3 occurrences in case of 5 candidates

#if CLI
        Console.Out.PrintLine(ConsoleColor.Yellow, $"ancestry: {ancestryCount} max-ancestry: {maxAncestryCount} ancestry-threshold: {ancestryThreshold}");
#endif
        Debug.WriteLine($"ancestry: {ancestryCount} max-ancestry: {maxAncestryCount} ancestry-threshold: {ancestryThreshold}");

        if (maxAncestryCount / (float)ancestryThreshold < 0.6f &&
            (ancestryCount == 0 || ancestryCount != maxAncestryCount))
        {
            // the top candidates are mostly unrelated, check their common ancestors

            var foundRelevantAncestor = false;
            var midTokenCount = GetMedianTokenCount(topCandidates.Keys);
            var maxTokenCount = topCandidates.Max(ca => ca.Key.TokenCount);

#if CLI
            Console.Out.PrintLine(ConsoleColor.Cyan, $"mid-tokens: {midTokenCount} max-tokens: {maxTokenCount}");
#endif
            Debug.WriteLine($"mid-tokens: {midTokenCount} max-tokens: {maxTokenCount}");

            foreach (var (ancestor, reoccurrence) in commonAncestors.OrderBy(ca => ca.Value).ThenByDescending(ca => ca.Key.NestingLevel))
            {
                if (!candidates.TryGetValue(ancestor, out var ancestorCandidate))
                    continue;

#if CLI
                Console.Out.PrintLine($"{ancestor.GetPath():blue}: " +
                    $"{reoccurrence:yellow} {ancestorCandidate.ContentScore:F2:magenta} " +
                    $"({ancestorCandidate.TokenCount}) [{ancestorCandidate.NestingLevel:yellow}]");
#endif
                Debug.WriteLine($"{ancestor.GetPath()}: {reoccurrence} {ancestorCandidate.ContentScore:F2} ({ancestorCandidate.TokenCount}) [{ancestorCandidate.NestingLevel}]");

                if (!foundRelevantAncestor && (
                    (reoccurrence == topCandidateCount && !topCandidates.ContainsValue(ancestor)) ||
                    (reoccurrence > ancestryThreshold && ancestorCandidate.TokenCount >= maxTokenCount) ||
                    (reoccurrence == ancestryThreshold && ((topCandidates.ContainsValue(ancestor) && maxAncestryCount > 0) || ancestor == topmostCandidate)) ||
                    (reoccurrence < ancestryThreshold && ancestor == topmostCandidate && ancestorCandidate.TokenCount >= midTokenCount)) &&
                    ancestorCandidate.CompareTo(articleCandidate) >= 0)
                {
                    // the ancestor candidate must have at least the same number of tokens as previous candidate
                    articleCandidate = ancestorCandidate;
                    foundRelevantAncestor = true;

                    //todo: DocumentReader break here
                }
            }
        }
        else if (HasOutlier(candidates.Values, out var outlier))
        {
            // the outlier candidate has much more content
            articleCandidate = outlier;
        }
        else if (ancestryCount / (float)ancestryThreshold > 0.6f)
        {
            // too many parents, find the first grandparent amoung the top candidates
            var grandparent = topCandidates.Keys[ancestryCount];
            if (articleCandidate.CompareTo(grandparent) <= 0)
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
#if CLI
            Console.Out.PrintLine($"\nArticle: {articleCandidate.Path:green} {articleCandidate.ContentScore:F2:magenta} ({articleCandidate.TokenCount})");
#endif
            Debug.WriteLine($"\nArticle: {articleCandidate.Path} {articleCandidate.ContentScore:F2} ({articleCandidate.TokenCount})");
            result = articleCandidate;
            return true;
        }

        result = default;
        return false;
    }

    private static int GetMedianTokenCount(IEnumerable<ArticleCandidate> topCandidates)
    {
        var candidates = topCandidates
            .Order(TokenCountComparer)
            .ToArray();

        var count = candidates.Length;
        var mid = count / 2;

        if (count % 2 != 0)
            return candidates[mid].TokenCount;

        return (candidates[mid - 1].TokenCount + candidates[mid].TokenCount) / 2;
    }

    public ArticleCandidate Elect()
    {
        var articleContent = this.Root;

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
        PrepArticle(articleContent, CleanFlags.All);

        return new(articleContent, this.TokenCount, this.ContentScore);
    }

    [Flags]
    private enum CleanFlags
    {
        None = 0,

        StripUnlikelys = 0x1,
        WeightClasses = 0x2,
        CleanConditionally = 0x4,

        All = StripUnlikelys | WeightClasses | CleanConditionally,
    }

    public static bool HasOutlier(IReadOnlyCollection<ArticleCandidate> allCandidates, [NotNullWhen(true)] out ArticleCandidate outlier)
    {
        var candidates = allCandidates
            .OrderDescending(TokenCountComparer)
            .DistinctBy(c => c.TokenCount)
            .ToArray();

        var lastIndex = candidates.Length - 1;
        if (lastIndex > 1)
        {
            for (var i = 0; i < lastIndex; ++i)
            {
                var ratio = candidates[i + 1].TokenCount / (float)candidates[i].TokenCount;
                if (ratio < 0.15f)
                {
                    outlier = candidates[i];
                    return true;
                }
            }
        }

        outlier = default;
        return false;
    }

    private static bool TryCountTokens(ParentTag root, out int tokenCount, out float tokenDensity)
    {
        if (root.HasOneChild || root.IsProbablyHidden() ||
            root.Category.HasFlag(ContentCategory.Metadata) || root.Category.HasFlag(ContentCategory.Script))
        {
            tokenCount = 0;
            tokenDensity = 0f;
            return false;
        }

        var tokenTotal = 0;
        var wordCount = 0;
        var numberCount = 0;
        var punctuationCount = 0;

        // direct content

        foreach (var element in root)
        {
            if (element is not Content content)
                continue;

            foreach (var token in content.Data.EnumerateTokens())
            {
                ++tokenTotal;

                if (token.Category == TokenCategory.Word)
                    ++wordCount;
                else if (token.Category == TokenCategory.Number)
                    ++numberCount;
                else if (token.Category == TokenCategory.PunctuationMark)
                    ++punctuationCount;
            }
        }

        if (tokenTotal > 0 && punctuationCount < (wordCount + numberCount))
        {
            // has some direct content
            tokenCount = wordCount + numberCount + punctuationCount;
            var directTokenDensity = (float)tokenCount / tokenTotal;

            if (directTokenDensity > 0f)
            {
                // ignore elements with direct content
                tokenCount = 0;
                tokenDensity = 0f;
                return false;
            }
        }

        // all content

        tokenCount = tokenTotal = wordCount = numberCount = punctuationCount = 0;
        foreach (var content in root.FindAll<Content>())
        {
            if (content.Parent is ParentTag parent &&
                (parent.Category.HasFlag(ContentCategory.Metadata) || parent.Category.HasFlag(ContentCategory.Script)))
            {
                continue;
            }

            foreach (var token in content.Data.EnumerateTokens())
            {
                ++tokenTotal;

                if (token.Category == TokenCategory.Word)
                    ++wordCount;
                else if (token.Category == TokenCategory.Number)
                    ++numberCount;
                else if (token.Category == TokenCategory.PunctuationMark)
                    ++punctuationCount;
            }
        }

        if (tokenTotal == 0 || punctuationCount >= (wordCount + numberCount))
        {
            // no content or non-content
            tokenCount = 0;
            tokenDensity = 0f;
            return false;
        }

        tokenCount = wordCount + numberCount + punctuationCount;
        tokenDensity = (float)tokenCount / tokenTotal;
        return true;
    }

    private static int CountMarkup(ParentTag root)
    {
        return root.FindAll<Tag>(IsNonContentElement).Count() + (IsNonContentElement(root) ? 1 : 0);

        static bool IsNonContentElement(Tag root)
        {
            if (!root.PermittedContent.HasFlag(ContentCategory.Phrasing) ||
                root.Category.HasFlag(ContentCategory.Metadata) ||
                root.Category.HasFlag(ContentCategory.Script) ||
                root.Category.HasFlag(ContentCategory.Form))
            {
                return true;
            }

            if (root is ParentTag { HasChildren: true } parent)
            {
                return parent.All<Tag>(tag => !tag.PermittedContent.HasFlag(ContentCategory.Phrasing) && (
                    !tag.Category.HasFlag(ContentCategory.Phrasing) ||
                    tag.Category.HasFlag(ContentCategory.Metadata) ||
                    tag.Category.HasFlag(ContentCategory.Script) ||
                    tag.Category.HasFlag(ContentCategory.Form)
                ));
            }

            return false;
        }
    }

    private static float GetElementFactor(ParentTag root, IRoot documentRoot)
    {
        var factor = GetElementFactor(root);
        for (var parent = root.Parent; parent is not null && parent != documentRoot; parent = parent.Parent)
        {
            factor *= GetElementFactor(parent);
        }
        return factor;
    }

    private static float GetElementFactor(ParentTag root)
    {
        var level = 0;
        var actual = root;
        while (actual.HasOneChild && actual.First() is ParentTag nested)
        {
            actual = nested;
            ++level;
        }

        var factor = KnownElementFactors.GetValueOrDefault(actual.Name, defaultValue: 1f);
        factor += GetElementWeight(actual);
        if (level > 0)
            factor -= 0.1f * (level + 1);

        return factor;
    }

    private static readonly FrozenDictionary<string, float> KnownElementFactors = new KeyValuePair<string, float>[]
    {
        new("article", 1.2f),
        new("section", 1.2f),
        new("div", 1.1f),
        new("main", 1.1f),
        new("pre", 0.9f),
        new("table", 0.9f),
        new("tbody", 0.9f),
        new("tr", 0.9f),
        new("td", 0.9f),
        new("ol", 0.8f),
        new("ul", 0.8f),
        new("dl", 0.8f),
        new("blockquote", 0.7f),
        new("dd", 0.7f),
        new("dt", 0.7f),
        new("li", 0.7f),
        new("form", 0.6f),
        new("address", 0.6f),
        new("p", 0.5f),
        new("h1", 0.5f),
        new("h2", 0.5f),
        new("h3", 0.5f),
        new("h4", 0.5f),
        new("h5", 0.5f),
        new("h6", 0.5f),
        new("hgroup", 0.5f),
        new("header", 0.5f),
        new("footer", 0.5f),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static float GetElementWeight(ParentTag tag)
    {
        var weight = 0f;

        if (tag.Attributes["class"] is { Length: > 0 } klass && TryGetNameWeight(klass, out var classWeight))
        {
            weight += classWeight;
        }

        if (tag.Attributes["id"] is { Length: > 0 } id && TryGetNameWeight(id, out var idWeight))
        {
            weight += idWeight;
        }

        if (tag.Attributes["name"] is { Length: > 0 } name && TryGetNameWeight(name, out var nameWeight))
        {
            weight += nameWeight;
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
                        weight -= 0.1f;
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
                        weight += 0.1f;
                        found = true;
                        goto Finish;
                    }
                }
            }

        Finish:
            return found;
        }
    }

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
        "widget",
        //"switcher",
        //"newsletter"
    ];

    private sealed class CandidateContentScoreComparer : IComparer<ArticleCandidate>
    {
        // ContentScore desc, Root.Offset desc
        public int Compare(ArticleCandidate x, ArticleCandidate y)
        {
            var result = y.ContentScore.CompareTo(x.ContentScore);
            if (result == 0)
                return y.Root.Offset.CompareTo(x.Root.Offset);
            return result;
        }
    }

    private sealed class CandidateTokenCountComparer : IComparer<ArticleCandidate>
    {
        // TokenCount asc, Root.NestingLevel desc
        public int Compare(ArticleCandidate x, ArticleCandidate y)
        {
            var result = x.TokenCount.CompareTo(y.TokenCount);
            if (result == 0 && x.Root.Parent != y.Root.Parent)
            {
                if (x.Root.Parent == y.Root)
                    return 1;
                else if (y.Root.Parent == x.Root)
                    return -1;
                else
                    return y.Root.NestingLevel.CompareTo(x.Root.NestingLevel);
            }
            return result;
        }
    }

    private static readonly string[] ShareElements = ["share", "sharedaddy"];

    private static void PrepArticle(ParentTag articleContent, CleanFlags cleanFlags)
    {
        CleanStyles(articleContent);

        // Check for data tables before we continue, to avoid removing items in
        // those tables, which will often be isolated even though they're
        // visually linked to other content-ful elements (text, images, etc.).
        MarkDataTables(articleContent);

        FixLazyImages(articleContent);

        // Clean out junk from the article content
        CleanConditionally(articleContent, "form", cleanFlags);
        CleanConditionally(articleContent, "fieldset", cleanFlags);
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
        CleanHeaders(articleContent, cleanFlags);

        // Do these last as the previous stuff may have removed junk
        // that will affect these
        CleanConditionally(articleContent, "table", cleanFlags);
        CleanConditionally(articleContent, "ul", cleanFlags);
        CleanConditionally(articleContent, "div", cleanFlags);

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
     * Remove the style attribute on every tag and under.
     *
     * @param Element
     * @return void
    **/
    private static readonly string[] PresentationalAttributes =
        ["align", "background", "bgcolor", "border", "cellpadding", "cellspacing", "frame", "hspace", "rules", "style", "valign", "vspace"];

    private static readonly string[] DeprecatedSizeAttributeElems = ["table", "th", "td", "hr", "pre"];

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
    // If the table has a descendant with any of these tags, consider a data table:
    private static readonly string[] DataTableDescendants = ["col", "colgroup", "tfoot", "thead", "th"];

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

        static (int Rows, int Columns) GetRowAndColumnCount(ParentTag table)
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
    // Commas as used in Latin, Sindhi, Chinese and various other scripts.
    // see: https://en.wikipedia.org/wiki/Comma#Comma_variants
    private static readonly SearchValues<char> Commas = SearchValues.Create("\u002C\u060C\uFE50\uFE10\uFE11\u2E41\u2E34\u2E32\uFF0C");

    private static void CleanConditionally(ParentTag articleContent, string tagName, CleanFlags cleanFlags)
    {
        // First check if this node IS data table, in which case don't remove it.
        static bool IsDataTable(ParentTag t)
        {
            return t.Attributes.Has("_readabilityDataTable", "true");
        }

        static float GetTextDensity(ParentTag e, Func<Tag, bool> selector)
        {
            var textLength = e.GetContentLength(true);
            if (textLength == 0) return 0f;

            var children = e.FindAll(selector);
            var childrenLength = children.Sum(child => child.GetContentLength(true));
            return (float)childrenLength / textLength;
        }

        /**
         * Get the density of links as a percentage of the content
         * This is the amount of text that is inside a link divided by the total text in the node.
         *
         * @param Element
         * @return number (float)
        **/
        // _getLinkDensity
        static float GetLinkDensity(ParentTag parent)
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

        if (!cleanFlags.HasFlag(CleanFlags.CleanConditionally))
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

            var weight = GetClassWeight(node, cleanFlags);
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

    /**
 * Get an elements class/id weight. Uses regular expressions to tell if this
 * element looks good or bad.
 *
 * @param Element
 * @return number (Integer)
**/
    // _getClassWeight
    private static float GetClassWeight(Tag tag, CleanFlags cleanFlags)
    {
        if (!cleanFlags.HasFlag(CleanFlags.WeightClasses))
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

    /**
     * Clean out spurious headers from an Element.
     *
     * @param Element
     * @return void
    **/
    private static void CleanHeaders(ParentTag articleContent, CleanFlags cleanFlags)
    {
        foreach (var heading in articleContent.FindAll<ParentTag>(h => h is { Name: "h1" or "h2" }).ToArray())
        {
            var shouldRemove = GetClassWeight(heading, cleanFlags) < 0;
            if (shouldRemove)
            {
                Debug.WriteLine($"Removing header with low class weight: {heading.Name}\n{heading.ToText()}\n");
                heading.Remove();
            }
        }
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

    /***
 * Determine if a node qualifies as phrasing content.
 * https://developer.mozilla.org/en-US/docs/Web/Guide/HTML/Content_categories#Phrasing_content
**/
    // _isPhrasingContent
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
}


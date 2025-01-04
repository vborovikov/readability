namespace Readability;

using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Brackets;
using FuzzyCompare.Text;

[DebuggerDisplay("{Path,nq}: {ContentScore} ({TokenCount})")]
readonly record struct ArticleCandidate
{
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

    public static bool TryCreate(ParentTag root, [NotNullWhen(true)] out ArticleCandidate candidate)
    {
        if (TryCountTokens(root, out var tokenCount, out var tokenDensity))
        {
            var markupCount = CountMarkup(root);
            var elementFactor = GetElementFactor(root);
            if (tokenCount > markupCount && (markupCount > 0 || elementFactor > 1f))
            {
                var contentScore = tokenCount / (markupCount + MathF.Log2(tokenCount)) * tokenDensity * elementFactor;

                Debug.WriteLine($"{root.GetPath()}: tokens: {tokenCount}, markup: {markupCount}, density: {tokenDensity}, factor: {elementFactor}, score: {contentScore}");

                candidate = new ArticleCandidate(root, tokenCount, contentScore);
                return true;
            }
        }

        candidate = default;
        return false;
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
        new("address", 0.8f),
        new("blockquote", 0.8f),
        new("ol", 0.8f),
        new("ul", 0.8f),
        new("dl", 0.8f),
        new("dd", 0.8f),
        new("dt", 0.8f),
        new("li", 0.8f),
        new("form", 0.8f),
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
        // ContentScore desc
        public int Compare(ArticleCandidate x, ArticleCandidate y) => y.ContentScore.CompareTo(x.ContentScore);
    }

    private sealed class CandidateTokenCountComparer : IComparer<ArticleCandidate>
    {
        // TokenCount asc, NestingLevel desc
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
}


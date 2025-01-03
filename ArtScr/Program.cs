namespace ArtScr;

using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Brackets;
using FuzzyCompare.Text;
using Readability;
using Termly;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("HTML file path expected");
            return 1;
        }

        var htmlFile = new FileInfo(args[0]);
        if (!htmlFile.Exists)
        {
            Console.Error.WriteLine($"File '{htmlFile.FullName}' doesn't exist");
            return 2;
        }

        try
        {
            await using var htmlFileStream = htmlFile.OpenRead();
            var document = await Document.Html.ParseAsync(htmlFileStream);

            var nbTopCandidates = args.Length > 1 && int.TryParse(args[1], out var num) ? num : DefaultNTopCandidates;

            var body = document
                .FirstOrDefault<ParentTag>(h => h.Name == "html")?
                .FirstOrDefault<ParentTag>(b => b.Name == "body") ??
                (IRoot)document;

            // find candidates with highest scores

            var candidates = new Dictionary<ParentTag, ArticleCandidate>();
            var contentScores = new PriorityQueue<ArticleCandidate, float>(nbTopCandidates);
            foreach (var root in body.FindAll<ParentTag>(p => p is { Layout: FlowLayout.Block, HasChildren: true }))
            {
                if (!TryCountTokens(root, out var tokenCount, out var tokenDensity))
                    continue;

                var markupCount = CountMarkup(root);
                var elementFactor = GetElementFactor(root);
                if (tokenCount > markupCount && (markupCount > 0 || elementFactor > 1f))
                {
                    var contentScore = tokenCount / (markupCount + MathF.Log2(tokenCount)) * tokenDensity * elementFactor;

                    Debug.WriteLine($"{GetElementPath(root)}: tokens: {tokenCount}, markup: {markupCount}, density: {tokenDensity}, factor: {elementFactor}, score: {contentScore}");

                    var newCandidate = new ArticleCandidate(root, tokenCount, contentScore);
                    candidates.Add(root, newCandidate);

                    if (contentScores.Count < nbTopCandidates)
                    {
                        contentScores.Enqueue(newCandidate, contentScore);

                    }
                    else
                    {
                        contentScores.EnqueueDequeue(newCandidate, contentScore);
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
                Console.Out.PrintLine($"{candidate.Path:cyan}: {score:F2:magenta} ({candidate.TokenCount})");
                Debug.WriteLine($"{candidate.Path}: {score:F2} ({candidate.TokenCount})");

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

            Console.Out.PrintLine(ConsoleColor.Yellow, $"ancestry: {ancestryCount} max-ancestry: {maxAncestryCount}");
            Debug.WriteLine($"ancestry: {ancestryCount} max-ancestry: {maxAncestryCount}");

            var ancestryThreshold = nbTopCandidates / 2 + nbTopCandidates % 2; // 3 occurrences in case of 5 candidates
            if (maxAncestryCount / (float)ancestryThreshold < 0.6f &&
                (ancestryCount == 0 || ancestryCount != maxAncestryCount))
            {
                // the top candidates are mostly unrelated, check their common ancestors

                var foundRelevantAncestor = false;
                var topmostCandidate = topCandidates.First().Value;
                var midTokenCount = GetMedianTokenCount(topCandidates.Keys);
                var maxTokenCount = topCandidates.Max(ca => ca.Key.TokenCount);
                foreach (var (ancestor, reoccurrence) in commonAncestors.OrderBy(ca => ca.Value).ThenByDescending(ca => ca.Key.NestingLevel))
                {
                    if (!candidates.TryGetValue(ancestor, out var ancestorCandidate))
                        continue;

                    Console.Out.PrintLine($"{GetElementPath(ancestor):blue}: {reoccurrence:yellow} {ancestorCandidate.ContentScore:F2:magenta} ({ancestorCandidate.TokenCount})");
                    Debug.WriteLine($"{GetElementPath(ancestor)}: {reoccurrence} {ancestorCandidate.ContentScore:F2} ({ancestorCandidate.TokenCount})");

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
            else if (HasOutlierCandidate(candidates.Values, out var outlier))
            {
                // the outlier candidate has much more content
                articleCandidate = outlier;
            }
            else if (ancestryCount >= ancestryThreshold)
            {
                // too many parents, find the first grandparent amoung the top candidates
                var grandparent = topCandidates.Keys[ancestryCount];
                var ratio = articleCandidate.TokenCount / (float)grandparent.TokenCount;
                if (ratio <= 0.75f)
                {
                    // the grandparent candidate has significantly more content
                    articleCandidate = grandparent;
                }
            }

            if (articleCandidate != default)
            {
                Console.Out.PrintLine($"\nArticle: {articleCandidate.Path:green} {articleCandidate.ContentScore:F2:magenta} ({articleCandidate.TokenCount})");
                Debug.WriteLine($"\nArticle: {articleCandidate.Path} {articleCandidate.ContentScore:F2} ({articleCandidate.TokenCount})");
            }
        }
        catch (Exception x)
        {
            Console.Error.WriteLine(ConsoleColor.DarkRed, x.Message);
            return 3;
        }

        return 0;
    }

    private static int GetMedianTokenCount(IList<ArticleCandidate> unsortedCandidates)
    {
        var candidates = unsortedCandidates.ToArray();
        Array.Sort(candidates, ArticleCandidate.TokenCountComparer);

        var count = candidates.Length;
        var mid = count / 2;

        if (count % 2 != 0)
            return candidates[mid].TokenCount;

        return (candidates[mid - 1].TokenCount + candidates[mid].TokenCount) / 2;
    }

    private static bool HasOutlierCandidate(IReadOnlyCollection<ArticleCandidate> allCandidates, [NotNullWhen(true)] out ArticleCandidate outlier)
    {
        var candidates = allCandidates
            .OrderDescending(ArticleCandidate.TokenCountComparer)
            .DistinctBy(c => c.TokenCount)
            .ToArray();

        var lastIndex = candidates.Length - 1;
        if (lastIndex > 1)
        {
            for (var i = 0; i < lastIndex; ++i)
            {
                var ratio = candidates[i + 1].TokenCount / (float)candidates[i].TokenCount;
                if (ratio < 0.1f)
                {
                    outlier = candidates[i];
                    return true;
                }
            }
        }

        outlier = default;
        return false;
    }

    [DebuggerDisplay("{Path,nq}: {ContentScore} ({TokenCount})")]
    private record struct ArticleCandidate(ParentTag Root, int TokenCount, float ContentScore)
    {
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

        public static readonly IComparer<ArticleCandidate> ConstentScoreComparer = new CandidateContentScoreComparer();
        public static readonly IComparer<ArticleCandidate> TokenCountComparer = new CandidateTokenCountComparer();

        public readonly string Path => GetElementPath(this.Root);
    }

    private static string GetElementPath(Tag? element)
    {
        if (element is null)
            return "/";

        var path = new StringBuilder(512);

        path.Append('/').Append(element.Name);
        for (var parent = element.Parent; parent is not null and not { Name: "body" or "head" or "html" }; parent = parent.Parent)
        {
            path.Insert(0, parent.Name).Insert(0, '/');
        }

        if (element.Attributes["id"] is { Length: > 0 } id)
        {
            path.Append('#').Append(id);
        }

        if (element.Attributes["name"] is { Length: > 0 } name)
        {
            path.Append('@').Append(name);
        }

        if (element.Attributes["class"] is { Length: > 0 } klass)
        {
            path.Append('[').Append(klass).Append(']');
        }

        return path.ToString();
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

    private const int DefaultNTopCandidates = 5;

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

    public static ValueEnumerator EnumerateValues(this ReadOnlySpan<char> span) => new(span);

    public ref struct ValueEnumerator
    {
        private ReadOnlySpan<char> span;
        private ReadOnlySpan<char> current;

        public ValueEnumerator(ReadOnlySpan<char> span)
        {
            this.span = span;
        }

        public readonly ReadOnlySpan<char> Current => this.current;

        public readonly ValueEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            var remaining = this.span;
            if (remaining.IsEmpty)
                return false;

            var start = remaining.IndexOfAnyExcept(' ');
            if (start >= 0)
            {
                remaining = remaining[start..];
                var end = remaining.IndexOf(' ');
                if (end > 0)
                {
                    this.current = remaining[..end];
                    this.span = remaining[(end + 1)..];
                    return true;
                }

                this.current = remaining;
                this.span = default;
                return true;
            }

            this.span = default;
            return false;
        }
    }
}

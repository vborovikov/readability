namespace ArtScr;

using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Brackets;
using FuzzyCompare.Text;
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
            var contentScores = new PriorityQueue<ArticleCandidate, float>(nbTopCandidates);

            var body = document
                .FirstOrDefault<ParentTag>(h => h.Name == "html")?
                .FirstOrDefault<ParentTag>(b => b.Name == "body") ??
                (IRoot)document;

            foreach (var root in body.FindAll<ParentTag>(p => p.Layout == FlowLayout.Block))
            {
                if (TryCountTokens(root, out var tokenCount, out var tokenFrequency))
                {
                    var markupCount = CountMarkup(root);
                    var elementFactor = GetElementFactor(root);
                    if (tokenCount > markupCount && (markupCount > 0 || elementFactor > 1f))
                    {
                        var contentScore = tokenCount / (markupCount + MathF.Log2(tokenCount)) * tokenFrequency * elementFactor;

                        Debug.WriteLine($"{GetElementPath(root)}: tokens: {tokenCount}, markup: {markupCount}, frequency: {tokenFrequency}, factor: {elementFactor}, score: {contentScore}");

                        if (contentScores.Count < nbTopCandidates)
                        {
                            contentScores.Enqueue(new(root, tokenCount), contentScore);

                        }
                        else
                        {
                            contentScores.EnqueueDequeue(new(root, tokenCount), contentScore);
                        }
                    }
                }
            }

            ParentTag? articleContent = null;
            var commonAncestors = new Dictionary<ParentTag, int>(nbTopCandidates);
            var candidates = new Dictionary<ParentTag, int>(contentScores.Count);
            var nestedGroupCount = 0;
            var maxTokenCount = 0;
            while (contentScores.TryDequeue(out var candidate, out var score))
            {
                Console.Out.WriteLineInColor($"{GetElementPath(candidate.Root):cyan}: {score:F2:magenta} ({candidate.TokenCount})");

                for (var parent = candidate.Root.Parent; parent is not null && parent != body; parent = parent.Parent)
                {
                    ref var reoccurence = ref CollectionsMarshal.GetValueRefOrAddDefault(commonAncestors, parent, out _);
                    ++reoccurence;
                }

                candidates.Add(candidate.Root, candidate.TokenCount);
                if (candidate.TokenCount > maxTokenCount)
                {
                    maxTokenCount = candidate.TokenCount;
                }
                if (candidate.Root.Parent == articleContent)
                {
                    ++nestedGroupCount;
                }
                else
                {
                    nestedGroupCount = 0;
                }

                articleContent = candidate.Root;
            }

            if (nestedGroupCount < 2)
            {
                var relevanceThreshold = nbTopCandidates / 2;
                var tokenCountThreshold = (int)(maxTokenCount * 0.2f); // 20% threshold
                var foundRelevantAncestor = false;
                foreach (var (ancestor, reoccurrence) in commonAncestors.OrderBy(ca => ca.Value).ThenByDescending(ca => ca.Key.NestingLevel))
                {
                    Console.Out.WriteLineInColor($"{GetElementPath(ancestor):blue}: {reoccurrence:yellow}");

                    if (!foundRelevantAncestor &&
                        (reoccurrence - relevanceThreshold == 1 || reoccurrence == nbTopCandidates) &&
                        (!candidates.TryGetValue(ancestor, out var tokenCount) || Math.Abs(maxTokenCount - tokenCount) < tokenCountThreshold))
                    {
                        articleContent = ancestor;
                        foundRelevantAncestor = true;
                    }
                }
            }

            if (articleContent is not null)
            {
                Console.Out.WriteLineInColor($"\nArticle: {GetElementPath(articleContent):green}");
            }
        }
        catch (Exception x)
        {
            Console.Error.WriteLine(x.Message);
            return 3;
        }

        return 0;
    }

    private record struct ArticleCandidate(ParentTag Root, int TokenCount);

    private static string GetElementPath(Tag element)
    {
        var path = new StringBuilder();

        path.Append('/').Append(element.Name);
        for (var parent = element.Parent; parent is not null and not { Name: "body" }; parent = parent.Parent)
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
        var tag = root;
        while (tag.Count() == 1 && tag.First() is ParentTag nested)
        {
            tag = nested;
        }

        var factor = KnownElementFactors.GetValueOrDefault(tag.Name, defaultValue: 1f);
        factor += GetElementWeight(tag);

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
        new("blockquote", 0.9f),
        new("address", 0.8f),
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

    private static bool TryCountTokens(ParentTag root, out int tokenCount, out float tokenFrequency)
    {
        var tokenTotal = 0;
        var wordCount = 0;
        var numberCount = 0;
        var punctuationCount = 0;

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
            tokenFrequency = 0f;
            return false;
        }

        tokenCount = (wordCount + numberCount + punctuationCount);
        tokenFrequency = (float)tokenCount / tokenTotal;
        return true;
    }

    private static int CountMarkup(ParentTag root)
    {
        return root.FindAll<Tag>(IsNonContentElement).Count() + (IsNonContentElement(root) ? 1 : 0);

        static bool IsNonContentElement(Tag tag)
        {
            return
                (!tag.PermittedContent.HasFlag(ContentCategory.Phrasing) || tag.Category.HasFlag(ContentCategory.Form)) ||
                (tag is ParentTag parent && parent.Any() &&
                    parent.All<Tag>(t => (!t.Category.HasFlag(ContentCategory.Phrasing) || t.Category.HasFlag(ContentCategory.Form)) &&
                        !t.PermittedContent.HasFlag(ContentCategory.Phrasing)));
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

namespace ArtScr;

using System.Diagnostics;
using System.Text;
using Brackets;
using FuzzyCompare.Text;

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

            var contentScores = new PriorityQueue<ParentTag, float>(DefaultNTopCandidates);

            var body = document
                .FirstOrDefault<ParentTag>(h => h.Name == "html")?
                .FirstOrDefault<ParentTag>(b => b.Name == "body") ??
                (IRoot)document;
            foreach (var root in body.FindAll<ParentTag>(p => p.Layout == FlowLayout.Block))
            {
                var score = GetContentScore(root);
                if (float.IsNormal(score))
                {
                    if (contentScores.Count == DefaultNTopCandidates)
                    {
                        contentScores.EnqueueDequeue(root, score);
                    }
                    else
                    {
                        contentScores.Enqueue(root, score);
                    }
                }
            }

            while (contentScores.TryDequeue(out var root, out var score))
            {
                Console.Out.WriteLine($"{GetElementPath(root)}: {score:F2}");
            }
        }
        catch (Exception x)
        {
            Console.Error.WriteLine(x.Message);
            return 3;
        }

        return 0;
    }

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

    private static float GetContentScore(ParentTag tag)
    {
        var (contentCount, frequency) = GetContentStats(tag);
        if (contentCount == 0)
        {
            // no content at all
            return 0f;
        }

        var nonContentCount = CountNonContent(tag);
        if (nonContentCount == 0)
        {
            // pure content
            return 0f;
        }

        var elementFactor = GetElementFactor(tag);

        Debug.WriteLine($"{GetElementPath(tag)}: # content: {contentCount}, # non-content: {nonContentCount}, frequency: {frequency}, factor: {elementFactor}");

        return (((float)contentCount / nonContentCount) * frequency) * elementFactor;
    }

    private static float GetElementFactor(ParentTag tag)
    {
        var factor = tag.Name switch
        {
            "article" or "section" => 1.2f,
            "div" => 1.1f,
            "pre" or "td" or "blockquote" => 1f,
            "address" or "ol" or "ul" or "dl" or "dd" or "dt" or "li" or "form" => 0.9f,
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => 0.5f,
            _ => 1f,
        };
        factor += GetElementWeight(tag);

        return factor;
    }

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

    private static (int Length, float Frequency) GetContentStats(ParentTag root)
    {
        var tokenCount = 0;
        var wordCount = 0;
        var numberCount = 0;
        var punctuationCount = 0;
        var contentLength = 0;

        foreach (var content in root.FindAll<Content>())
        {
            if (content.Parent is ParentTag parent &&
                (parent.Category.HasFlag(ContentCategory.Metadata) || parent.Category.HasFlag(ContentCategory.Script)))
            {
                continue;
            }

            contentLength += content.Length;

            foreach (var token in content.Data.EnumerateTokens())
            {
                ++tokenCount;
                if (token.Category == TokenCategory.Word)
                    ++wordCount;
                if (token.Category == TokenCategory.Number)
                    ++numberCount;
                if (token.Category == TokenCategory.PunctuationMark)
                    ++punctuationCount;
            }
        }

        if (tokenCount == 0)
            return default;

        return (tokenCount, (float)(wordCount + numberCount + punctuationCount) / tokenCount);
    }

    private static int CountNonContent(ParentTag root)
    {
        return root.FindAll<Tag>(IsNonContentElement).Count();
    }

    private static bool IsNonContentElement(Tag tag)
    {
        return tag.PermittedContent != ContentCategory.Phrasing &&
            (tag is ParentTag root && root.Any() && root.All<Tag>(t => t.PermittedContent != ContentCategory.Phrasing));
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
        "widget"
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

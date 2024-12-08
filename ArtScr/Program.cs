﻿namespace ArtScr;

using System.Collections.Frozen;
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

            var nbTopCandidates = args.Length > 1 && int.TryParse(args[1], out var num) ? num : DefaultNTopCandidates;
            var contentScores = new PriorityQueue<ParentTag, float>(nbTopCandidates);

            var body = document
                .FirstOrDefault<ParentTag>(h => h.Name == "html")?
                .FirstOrDefault<ParentTag>(b => b.Name == "body") ??
                (IRoot)document;
            foreach (var root in body.FindAll<ParentTag>(p => p.Layout == FlowLayout.Block))
            {
                var score = GetContentScore(root);
                if (float.IsNormal(score))
                {
                    if (contentScores.Count < nbTopCandidates)
                    {
                        contentScores.Enqueue(root, score);
                    }
                    else
                    {
                        contentScores.EnqueueDequeue(root, score);
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
        var elementFactor = GetElementFactor(tag);
        var contentScore = 0f;
        if (nonContentCount < 2)
        {
            // pure content

            if (contentCount < 50 || elementFactor > 1f)
            {
                // some element full of content
                elementFactor *= 1f / (1f + MathF.Exp(-nonContentCount / (float)contentCount));
                contentScore = contentCount * frequency * elementFactor;
            }
            else
            {
                // definitely a paragraph, almost always we want its parent
                contentScore = frequency * elementFactor;
            }
            Debug.WriteLine($"{GetElementPath(tag)}: content: {contentCount}, non-content: {nonContentCount}, frequency: {frequency}, factor: {elementFactor}, score: {contentScore}");
            return contentScore;
        }
        else if (contentCount == nonContentCount)
        {
            // noise
            return 0f;
        }

        // reduce the element factor by a bias value
        //elementFactor *= 1f - MathF.Log10(nonContentCount) / MathF.Log10(contentCount);
        
        // inverse logit
        //elementFactor *= 1f / (1f + MathF.Exp(-nonContentCount / (float)contentCount));
        
        contentScore = (((float)contentCount / nonContentCount) * frequency) * elementFactor;
        Debug.WriteLine($"{GetElementPath(tag)}: content: {contentCount}, non-content: {nonContentCount}, frequency: {frequency}, factor: {elementFactor}, score: {contentScore}");
        return contentScore;
    }

    private static float GetElementFactor(ParentTag tag)
    {
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
                else if (token.Category == TokenCategory.Number)
                    ++numberCount;
                else if (token.Category == TokenCategory.PunctuationMark)
                    ++punctuationCount;
            }
        }

        if (tokenCount == 0)
        {
            // no content
            return default;
        }

        if (punctuationCount >= (wordCount + numberCount))
        {
            // non-content
            return default;
        }

        var contentCount = (wordCount + numberCount + punctuationCount);
        return (contentCount, (float)contentCount / tokenCount);
    }

    private static int CountNonContent(ParentTag root)
    {
        return root.FindAll<Tag>(IsNonContentElement).Count() +
            (IsNonContentElement(root) ? 1 : 0);

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

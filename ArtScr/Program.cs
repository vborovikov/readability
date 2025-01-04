namespace ArtScr;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Brackets;
using Readability;
using Termly;

static partial class Program
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
                if (!ArticleCandidate.TryCreate(root, out var candidate))
                    continue;

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

                    Console.Out.PrintLine($"{ancestor.GetPath():blue}: {reoccurrence:yellow} {ancestorCandidate.ContentScore:F2:magenta} ({ancestorCandidate.TokenCount})");
                    Debug.WriteLine($"{ancestor.GetPath()}: {reoccurrence} {ancestorCandidate.ContentScore:F2} ({ancestorCandidate.TokenCount})");

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

    private static int GetMedianTokenCount(IList<ArticleCandidate> topCandidates)
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

    private const int DefaultNTopCandidates = 5;

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

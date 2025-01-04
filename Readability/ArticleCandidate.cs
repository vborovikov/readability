namespace Readability;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Brackets;

[DebuggerDisplay("{Path,nq}: {ContentScore} ({TokenCount})")]
readonly record struct ArticleCandidate
{
    public ArticleCandidate(ParentTag root, int tokenCount, float contentScore)
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

    public static bool TryCreate(ParentTag root, [NotNullWhen(true)] out ArticleCandidate candidate)
    {
        candidate = default;
        return false;
    }

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


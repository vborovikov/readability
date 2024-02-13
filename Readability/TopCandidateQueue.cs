namespace Readability;

sealed class TopCandidateQueue
{
    private readonly int maxCount;

    public TopCandidateQueue(int maxCount)
    {
        this.maxCount = maxCount;
    }
}
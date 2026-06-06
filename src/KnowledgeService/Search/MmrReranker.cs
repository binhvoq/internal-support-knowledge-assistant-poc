namespace SupportPoc.KnowledgeService.Search;

public sealed class MmrReranker
{
    public IReadOnlyList<T> Select<T>(
        IReadOnlyList<T> candidates,
        IReadOnlyList<float> queryEmbedding,
        Func<T, IReadOnlyList<float>> embeddingSelector,
        int top,
        double lambda = 0.7)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        ArgumentNullException.ThrowIfNull(embeddingSelector);

        if (top <= 0 || candidates.Count == 0)
            return [];

        if (lambda is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(lambda), lambda, "MMR lambda must be between 0 and 1.");

        if (queryEmbedding.Count == 0)
            throw new ArgumentException("Query embedding must not be empty.", nameof(queryEmbedding));

        var candidateEmbeddings = candidates.Select(embeddingSelector).ToArray();
        for (var i = 0; i < candidateEmbeddings.Length; i++)
            ValidateEmbedding(candidateEmbeddings[i], queryEmbedding.Count, $"Candidate embedding at index {i}");

        var querySimilarities = candidateEmbeddings
            .Select(candidateEmbedding => CosineSimilarity(queryEmbedding, candidateEmbedding))
            .ToArray();

        var selectedIndexes = new List<int>(Math.Min(top, candidates.Count));
        var remainingIndexes = Enumerable.Range(0, candidates.Count).ToHashSet();

        while (selectedIndexes.Count < top && remainingIndexes.Count > 0)
        {
            var bestIndex = -1;
            var bestScore = double.NegativeInfinity;

            foreach (var candidateIndex in remainingIndexes)
            {
                var relevance = querySimilarities[candidateIndex];
                var redundancy = selectedIndexes.Count == 0
                    ? 0
                    : selectedIndexes.Max(selectedIndex =>
                        CosineSimilarity(candidateEmbeddings[candidateIndex], candidateEmbeddings[selectedIndex]));
                var score = (lambda * relevance) - ((1 - lambda) * redundancy);

                if (score > bestScore)
                {
                    bestIndex = candidateIndex;
                    bestScore = score;
                }
            }

            selectedIndexes.Add(bestIndex);
            remainingIndexes.Remove(bestIndex);
        }

        return selectedIndexes.Select(index => candidates[index]).ToList();
    }

    public static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count != right.Count)
            throw new ArgumentException("Vectors must have the same dimensions.", nameof(right));

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;

        for (var i = 0; i < left.Count; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        if (leftNorm == 0 || rightNorm == 0)
            return 0;

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    private static void ValidateEmbedding(IReadOnlyList<float> embedding, int dimensions, string name)
    {
        if (embedding.Count != dimensions)
            throw new ArgumentException($"{name} has {embedding.Count} dimensions, expected {dimensions}.");
    }
}

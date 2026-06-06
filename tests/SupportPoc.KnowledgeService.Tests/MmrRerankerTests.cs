using SupportPoc.KnowledgeService.Search;

namespace SupportPoc.KnowledgeService.Tests;

public sealed class MmrRerankerTests
{
    private readonly MmrReranker _reranker = new();

    [Fact]
    public void Select_keeps_wfh_context_diverse_instead_of_repeating_two_day_policy()
    {
        var query = Vector(1.0f, 0.8f, 0.7f);
        var candidates = new[]
        {
            Candidate("wfh-2-days", "WFH toi da 2 ngay moi tuan", "policy", Vector(1.0f, 0.0f, 0.0f)),
            Candidate("wfh-2-days-duplicate", "Work from home 2 days per week", "policy", Vector(0.98f, 0.04f, 0.0f)),
            Candidate("wfh-2-days-overlap", "WFH toi da hai ngay, lap lai tu chunk overlap", "policy", Vector(0.96f, 0.03f, 0.0f)),
            Candidate("wfh-registration-condition", "Dieu kien dang ky WFH can bao quan ly truoc", "condition", Vector(0.0f, 1.0f, 0.0f)),
            Candidate("wfh-approval-process", "Quy trinh duyet WFH tren HR portal", "process", Vector(0.0f, 0.0f, 1.0f))
        };

        var selected = _reranker.Select(candidates, query, candidate => candidate.Embedding, top: 3, lambda: 0.7);

        Assert.Equal(3, selected.Count);
        Assert.Equal("policy", selected[0].Concept);
        Assert.Single(selected, candidate => candidate.Concept == "policy");
        Assert.Contains(selected, candidate => candidate.Concept == "condition");
        Assert.Contains(selected, candidate => candidate.Concept == "process");
    }

    [Fact]
    public void Select_keeps_fruit_results_from_being_filled_by_orange_variants()
    {
        var query = Vector(1.0f, 0.8f, 0.8f, 0.0f);
        var candidates = new[]
        {
            Candidate("orange-thai", "Cam Thai Lan", "orange", Vector(1.0f, 0.0f, 0.0f, 0.0f)),
            Candidate("orange-us", "Cam My", "orange", Vector(0.98f, 0.03f, 0.0f, 0.0f)),
            Candidate("orange-thai-grade-1", "Cam Thai Lan loai 1", "orange", Vector(0.96f, 0.02f, 0.0f, 0.0f)),
            Candidate("watermelon-red", "Dua hau ruot do", "watermelon", Vector(0.0f, 1.0f, 0.0f, 0.0f)),
            Candidate("strawberry", "Dau tay", "strawberry", Vector(0.0f, 0.0f, 1.0f, 0.0f)),
            Candidate("mango", "Xoai", "mango", Vector(0.0f, 0.0f, 0.0f, 1.0f))
        };

        var selected = _reranker.Select(candidates, query, candidate => candidate.Embedding, top: 3, lambda: 0.7);

        Assert.Equal(3, selected.Count);
        Assert.Single(selected, candidate => candidate.Concept == "orange");
        Assert.Contains(selected, candidate => candidate.Concept == "watermelon");
        Assert.Contains(selected, candidate => candidate.Concept == "strawberry");
        Assert.DoesNotContain(selected, candidate => candidate.Concept == "mango");
    }

    private static TestCandidate Candidate(string id, string text, string concept, IReadOnlyList<float> embedding) =>
        new(id, text, concept, embedding);

    private static float[] Vector(params float[] values) => values;

    private sealed record TestCandidate(
        string Id,
        string Text,
        string Concept,
        IReadOnlyList<float> Embedding);
}

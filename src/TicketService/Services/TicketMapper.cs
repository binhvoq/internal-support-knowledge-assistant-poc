using System.Text.Json;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Services;

internal static class TicketMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TicketDto ToDto(TicketEntity entity) => new()
    {
        Id = entity.Id,
        EmployeeId = entity.EmployeeId,
        Category = entity.Category,
        Question = entity.Question,
        Status = entity.Status,
        AiSuggestedAnswer = entity.AiSuggestedAnswer,
        FinalAnswer = entity.FinalAnswer,
        SagaStopNote = entity.SagaStopNote,
        RelatedDocuments = JsonSerializer.Deserialize<List<RelatedDocument>>(entity.RelatedDocumentsJson, JsonOptions) ?? [],
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };
}

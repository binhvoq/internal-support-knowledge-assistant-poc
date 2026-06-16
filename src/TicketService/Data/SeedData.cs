using System.Text.Json;
using SupportPoc.Shared.Models;

namespace SupportPoc.TicketService.Data;

public static class SeedData
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<TicketEntity> Tickets
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return
            [
                new()
                {
                    Id = "0196000100017f8a81b2c3d4e5f67801",
                    EmployeeId = "nguyen.an@company.com",
                    Category = SupportCategory.IT,
                    Question = "Khong ket noi duoc VPN sau khi doi mat khau hom qua.",
                    Status = TicketStatus.New,
                    CreatedAt = now.AddHours(-2),
                    UpdatedAt = now.AddHours(-2),
                },
                new()
                {
                    Id = "0196000100027f8a81b2c3d4e5f67802",
                    EmployeeId = "tran.b@company.com",
                    Category = SupportCategory.HR,
                    Question = "Muon dang ky nghi phep 3 ngay cuoi thang, can lam gi?",
                    Status = TicketStatus.Suggested,
                    AiSuggestedAnswer = """
                        Theo chinh sach nghi phep nam (DOC-002):
                        - Dang ky tren HR portal truoc it nhat 3 ngay lam viec.
                        - Truong bo phan phe duyet trong 2 ngay.
                        - Con 8 ngay phep trong nam hien tai.
                        """,
                    RelatedDocumentsJson = SerializeRelated(
                    [
                        new RelatedDocument
                        {
                            DocumentId = "DOC-002",
                            Title = "Chinh sach nghi phep nam",
                            Score = 0.91,
                        },
                    ]),
                    CreatedAt = now.AddHours(-5),
                    UpdatedAt = now.AddHours(-1),
                },
                new()
                {
                    Id = "0196000100037f8a81b2c3d4e5f67803",
                    EmployeeId = "le.c@company.com",
                    Category = SupportCategory.Finance,
                    Question = "Reimburse hoa don taxi ngay 10/6 chua duoc duyet.",
                    Status = TicketStatus.Resolved,
                    AiSuggestedAnswer = "Upload hoa don trong 30 ngay va theo doi tren Finance portal.",
                    FinalAnswer = "Da xac nhan ho so reimburse, Finance se chuyen khoan trong 3 ngay lam viec.",
                    RelatedDocumentsJson = SerializeRelated(
                    [
                        new RelatedDocument
                        {
                            DocumentId = "DOC-003",
                            Title = "Quy trinh reimburse chi phi",
                            Score = 0.88,
                        },
                    ]),
                    CreatedAt = now.AddDays(-2),
                    UpdatedAt = now.AddHours(-8),
                },
                new()
                {
                    Id = "0196000100047f8a81b2c3d4e5f67804",
                    EmployeeId = "pham.d@company.com",
                    Category = SupportCategory.IT,
                    Question = "Can cap quyen Figma cho team design.",
                    Status = TicketStatus.Suggested,
                    AiSuggestedAnswer = """
                        Tao ticket IT kem ten phan mem va ly do.
                        Manager phe duyet qua email; IT cap license trong 2 ngay lam viec (DOC-004).
                        """,
                    RelatedDocumentsJson = SerializeRelated(
                    [
                        new RelatedDocument
                        {
                            DocumentId = "DOC-004",
                            Title = "Huong dan cap quyen phan mem",
                            Score = 0.86,
                        },
                    ]),
                    CreatedAt = now.AddHours(-12),
                    UpdatedAt = now.AddHours(-3),
                },
                new()
                {
                    Id = "0196000100057f8a81b2c3d4e5f67805",
                    EmployeeId = "hoang.e@company.com",
                    Category = SupportCategory.IT,
                    Question = "May tinh khong vao duoc WiFi van phong sau khi cap nhat Windows.",
                    Status = TicketStatus.Reopened,
                    AiSuggestedAnswer = "Thu xoa profile WiFi cu va dang nhap lai bang tai khoan domain.",
                    FinalAnswer = "Da huong dan reset WiFi profile; van loi — chuyen cho IT onsite.",
                    CreatedAt = now.AddDays(-4),
                    UpdatedAt = now.AddHours(-6),
                },
            ];
        }
    }

    private static string SerializeRelated(IReadOnlyList<RelatedDocument> related) =>
        JsonSerializer.Serialize(related, JsonOptions);
}

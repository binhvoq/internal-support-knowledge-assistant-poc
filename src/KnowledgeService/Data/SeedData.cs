namespace SupportPoc.KnowledgeService.Data;

public static class SeedData
{
    public static IReadOnlyList<KnowledgeDocumentEntity> Documents =>
    [
        new()
        {
            Id = "DOC-001",
            Title = "Huong dan reset mat khau VPN",
            Category = "IT",
            Content = """
                Buoc 1: Mo portal VPN tai https://vpn.company.internal
                Buoc 2: Chon Forgot Password
                Buoc 3: Xac thuc qua email cong ty
                Buoc 4: Dat mat khau moi theo chinh sach bao mat (toi thieu 12 ky tu)
                Buoc 5: Dang nhap lai VPN client
                """,
            SourceUrl = "internal://it/vpn-reset",
            UpdatedAt = DateTimeOffset.UtcNow
        },
        new()
        {
            Id = "DOC-002",
            Title = "Chinh sach nghi phep nam",
            Category = "HR",
            Content = """
                Nhan vien chinh thuc duoc 12 ngay phep/nam.
                Dang ky nghi qua HR portal truoc it nhat 3 ngay lam viec.
                Truong bo phan phe duyet trong vong 2 ngay.
                """,
            SourceUrl = "internal://hr/leave-policy",
            UpdatedAt = DateTimeOffset.UtcNow
        },
        new()
        {
            Id = "DOC-003",
            Title = "Quy trinh reimburse chi phi",
            Category = "Finance",
            Content = """
                Upload hoa don trong vong 30 ngay ke tu ngay chi tieu.
                Dien form reimburse tren Finance portal.
                Phong Finance xu ly trong 5-7 ngay lam viec.
                """,
            SourceUrl = "internal://finance/reimburse",
            UpdatedAt = DateTimeOffset.UtcNow
        },
        new()
        {
            Id = "DOC-004",
            Title = "Huong dan cap quyen phan mem",
            Category = "IT",
            Content = """
                Tao ticket IT voi ten phan mem va ly do su dung.
                Manager phe duyet qua email.
                IT cai dat hoac cap license trong 2 ngay lam viec.
                """,
            SourceUrl = "internal://it/software-access",
            UpdatedAt = DateTimeOffset.UtcNow
        }
    ];
}

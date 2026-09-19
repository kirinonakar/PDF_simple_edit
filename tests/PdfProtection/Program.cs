using System.Text;
using iText.Kernel.Pdf;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new Exception(message);
}

byte[] plain;
using (var stream = new MemoryStream())
{
    using (var doc = new PdfDocument(new PdfWriter(stream)))
    {
        doc.AddNewPage();
        doc.AddNewPage();
        doc.GetDocumentInfo().SetTitle("Protection regression");
    }
    plain = stream.ToArray();
}
var security = new PdfSecurityService();
var files = new PdfFileOperationService();
string folder = Path.Combine(Path.GetTempPath(), "pdf-protection-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    foreach (var algorithm in Enum.GetValues<PdfEncryptionAlgorithm>())
    foreach (bool encryptMetadata in new[] { true, false })
    {
        var settings = new PdfProtectionSettings
        {
            Enabled = true, Algorithm = algorithm, EncryptMetadata = encryptMetadata,
            RequireOpenPassword = true, OpenPassword = "열기-pass-1",
            RequireOwnerPassword = true, OwnerPassword = "권한-pass-2",
            Permissions = EncryptionConstants.ALLOW_DEGRADED_PRINTING | EncryptionConstants.ALLOW_SCREENREADERS
        };
        byte[] encrypted = PdfSecurityService.Encrypt(plain, settings);
        Check(security.Open(encrypted, null).Status == PdfOpenStatus.PasswordRequired, "Missing password must fail");
        Check(security.Open(encrypted, "wrong").Status == PdfOpenStatus.WrongPassword, "Wrong password must fail");
        var opened = security.Open(encrypted, settings.OpenPassword);
        Check(opened.IsSuccess && opened.WasEncrypted, "User password must open");
        Check(opened.Protection!.OwnerPassword == null && opened.Protection.NeedsPasswords,
            "User credentials must never be substituted for unknown owner credentials");
        Check(opened.Protection.OpenPassword == settings.OpenPassword, "Keep user password");
        Check(opened.Protection.Algorithm == algorithm && opened.Protection.EncryptMetadata == encryptMetadata,
            "Keep algorithm and metadata flags");
        Check((opened.Permissions & EncryptionConstants.ALLOW_COPY) == 0, "Copy must remain prohibited");
        Check((opened.Permissions & EncryptionConstants.ALLOW_DEGRADED_PRINTING) != 0, "Low quality print permission");
        var ownerOpened = security.Open(encrypted, settings.OwnerPassword);
        Check(ownerOpened.IsSuccess && ownerOpened.Protection!.OwnerPassword == settings.OwnerPassword, "Owner credentials");
        Check(ownerOpened.Protection!.OpenPassword == (algorithm == PdfEncryptionAlgorithm.Aes128 ? settings.OpenPassword : null),
            "Recover AES128 user password only when available");
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(opened.Bytes!))))
        {
            Check(!doc.GetReader().IsEncrypted(), "Editing snapshot must be plaintext");
            Check(doc.GetNumberOfPages() == 2 && doc.GetDocumentInfo().GetTitle() == "Protection regression", "Content preserved");
        }
        using (var reader = new PdfReader(new MemoryStream(encrypted), new ReaderProperties().SetPassword(Encoding.UTF8.GetBytes(settings.OpenPassword))))
        using (var doc = new PdfDocument(reader))
            Check(!reader.IsOpenedWithFullPermission(), "Open password must not confer owner authority");

        var changed = settings with { OpenPassword = "new-open", OwnerPassword = "new-owner" };
        byte[] changedBytes = PdfSecurityService.Encrypt(opened.Bytes!, changed);
        Check(security.Open(changedBytes, settings.OpenPassword).Status == PdfOpenStatus.WrongPassword, "Old open password removed");
        Check(security.Open(changedBytes, settings.OwnerPassword).Status == PdfOpenStatus.WrongPassword, "Old owner password removed");
        Check(security.Open(changedBytes, changed.OpenPassword).IsSuccess && security.Open(changedBytes, changed.OwnerPassword).IsSuccess, "New passwords work");

        var ownerOnly = settings with { RequireOpenPassword = false, OpenPassword = null };
        byte[] ownerOnlyBytes = PdfSecurityService.Encrypt(plain, ownerOnly);
        var ownerOnlyOpened = security.Open(ownerOnlyBytes, null);
        Check(ownerOnlyOpened.IsSuccess && ownerOnlyOpened.WasEncrypted && !ownerOnlyOpened.PasswordRequiredToOpen, "Remove open password only");
        Check((ownerOnlyOpened.Permissions & EncryptionConstants.ALLOW_COPY) == 0, "Owner-only restrictions retained");
        var userOnly = settings with { RequireOwnerPassword = false, OwnerPassword = null, Permissions = PdfSecurityService.AllPermissions };
        Check(security.Open(PdfSecurityService.Encrypt(plain, userOnly), settings.OpenPassword).IsSuccess, "Remove owner password only");
        byte[] removed = PdfSecurityService.Encrypt(opened.Bytes!, new PdfProtectionSettings());
        Check(security.Open(removed, null) is { IsSuccess: true, WasEncrypted: false }, "Remove all protection");

        string source = Path.Combine(folder, "source.pdf");
        File.WriteAllBytes(source, plain);
        string merged = Path.Combine(folder, "merged.pdf");
        Check(await files.MergeAsync(plain, new[] { source }, merged, PdfSecurityService.CreateEncryptionProperties(settings)), "Merge succeeds");
        Check(security.Open(File.ReadAllBytes(merged), null).Status == PdfOpenStatus.PasswordRequired, "Merged file protected");
        Check(await files.SplitAsync(plain, source, folder, new[] { (1, 1), (2, 2) }, PdfSecurityService.CreateEncryptionProperties(settings)) == 2, "Split succeeds");
        foreach (string path in new[] { merged, Path.Combine(folder, "source_1.pdf"), Path.Combine(folder, "source_2.pdf") })
        {
            var result = security.Open(File.ReadAllBytes(path), settings.OwnerPassword);
            Check(result.IsSuccess && (result.Permissions & EncryptionConstants.ALLOW_COPY) == 0, "Derived files preserve owner password and restrictions");
        }
        string exported = Path.Combine(folder, "exported.pdf");
        Check(await files.ExportPagesAsync(plain, exported, new[] { 1 }, PdfSecurityService.CreateEncryptionProperties(settings)), "Export succeeds");
        Check(security.Open(File.ReadAllBytes(exported), settings.OpenPassword).IsSuccess, "Export password works");
        Check((settings with { OwnerPassword = settings.OpenPassword }).GetValidationError() != null, "Reject identical passwords");
        Check((settings with { OpenPassword = new string('한', 50) }).GetValidationError() != null, "Reject silent password truncation");
        Check((settings with { OwnerPassword = null }).GetValidationError() != null, "Reject missing password");
        Check((settings with { RequireOwnerPassword = false }).GetValidationError() != null, "Restrictions require owner password");
        // iText returns signed permission flags; negative must not mean allow everything.
        var signedPermissions = settings with { Permissions = opened.Permissions };
        var signedResult = security.Open(PdfSecurityService.Encrypt(plain, signedPermissions), settings.OpenPassword);
        Check((signedResult.Permissions & EncryptionConstants.ALLOW_COPY) == 0, "Signed permissions preserved on resave");
    }
    Console.WriteLine($"PASS: {assertions} PDF protection assertions.");
}
finally
{
    Directory.Delete(folder, recursive: true);
}

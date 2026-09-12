using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PDF_simple_edit.Services;

public enum PdfOpenStatus
{
    Success,
    PasswordRequired,
    WrongPassword,
    Failed
}

public sealed record PdfOpenResult(
    PdfOpenStatus Status,
    byte[]? Bytes = null,
    string? Password = null,
    bool WasEncrypted = false,
    bool PasswordRequiredToOpen = false,
    int Permissions = -1)
{
    public bool IsSuccess => Status == PdfOpenStatus.Success;
}

/// <summary>
/// 암호로 보호된 PDF를 다루기 위한 서비스.
///
/// 편집기 전체가 평문 바이트 스냅샷을 기반으로 동작하므로, 암호가 걸린 문서는
/// 열 때 한 번 복호화된 바이트로 정규화한다. 저장할 때는 원래의 열기 암호와 권한
/// 설정을 최대한 그대로 복원해 다시 암호화한다.
/// </summary>
public sealed class PdfSecurityService
{
    public const int AllPermissions =
        EncryptionConstants.ALLOW_PRINTING |
        EncryptionConstants.ALLOW_MODIFY_CONTENTS |
        EncryptionConstants.ALLOW_COPY |
        EncryptionConstants.ALLOW_MODIFY_ANNOTATIONS |
        EncryptionConstants.ALLOW_FILL_IN |
        EncryptionConstants.ALLOW_SCREENREADERS |
        EncryptionConstants.ALLOW_ASSEMBLY |
        EncryptionConstants.ALLOW_DEGRADED_PRINTING;

    public PdfOpenResult Open(byte[] source, string? password)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(password))
            password = null;

        bool encrypted;
        int permissions = -1;
        try
        {
            // iText는 문서를 실제로 열 때 암호를 검사하므로, 읽기 모드로 한 번 열어
            // 자격 증명을 먼저 검증한다. 이 단계에서는 unethicalReading을 사용하지
            // 않으므로 잘못된 암호나 누락된 암호는 반드시 예외가 된다.
            using var reader = new PdfReader(new MemoryStream(source), CreateReaderProperties(password));
            using var document = new PdfDocument(reader);
            encrypted = reader.IsEncrypted();
            if (encrypted)
                permissions = reader.GetPermissions();
        }
        catch (BadPasswordException)
        {
            return new PdfOpenResult(password is null ? PdfOpenStatus.PasswordRequired : PdfOpenStatus.WrongPassword);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PDF 열기 실패: {ex.Message}");
            return new PdfOpenResult(PdfOpenStatus.Failed);
        }

        if (!encrypted)
            return new PdfOpenResult(PdfOpenStatus.Success, source);

        try
        {
            bool passwordRequired = RequiresPasswordToOpen(source);
            byte[] plainBytes = Decrypt(source, password);
            return new PdfOpenResult(PdfOpenStatus.Success, plainBytes, password, true, passwordRequired, permissions);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PDF 복호화 실패: {ex.Message}");
            return new PdfOpenResult(PdfOpenStatus.Failed);
        }
    }

    public static byte[] Encrypt(byte[] plainBytes, string? userPassword, string? ownerPassword, int permissions)
    {
        ArgumentNullException.ThrowIfNull(plainBytes);

        using var reader = new PdfReader(new MemoryStream(plainBytes));
        using var output = new MemoryStream();
        using (var writer = new PdfWriter(output, CreateEncryptionProperties(userPassword, ownerPassword, permissions)))
        using (var document = new PdfDocument(reader, writer, new StampingProperties()))
        {
        }
        return output.ToArray();
    }

    public static WriterProperties CreateEncryptionProperties(string? userPassword, string? ownerPassword, int permissions)
    {
        var properties = new WriterProperties();
        properties.SetStandardEncryption(
            ToPasswordBytes(userPassword),
            ToPasswordBytes(ownerPassword),
            permissions >= 0 ? permissions : AllPermissions,
            EncryptionConstants.ENCRYPTION_AES_256);
        return properties;
    }

    private static bool RequiresPasswordToOpen(byte[] source)
    {
        try
        {
            using var reader = new PdfReader(new MemoryStream(source));
            using var document = new PdfDocument(reader);
            return false;
        }
        catch (BadPasswordException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Decrypt(byte[] source, string? password)
    {
        try
        {
            return Rewrite(source, password, unethicalReading: false);
        }
        catch (BadPasswordException)
        {
            // iText는 소유자 암호 없이 암호화된 문서를 비암호화 상태로 다시 쓸 수
            // 없다("PdfReader is not opened with owner password"). 이 편집기는
            // 평문 바이트 스냅샷 위에서 동작하므로 열 때 한 번 복호화가 필요하다.
            // 자격 증명은 위에서 이미 검증되었고(사용자가 열기 암호를 제공했거나
            // 열기 암호가 없는 문서), PDF 권한 플래그는 권고 사항이므로, 소유자
            // 암호를 요구해 문서를 아예 열 수 없게 되는 대신 unethicalReading
            // 플래그로 복호화를 진행한다.
            return Rewrite(source, password, unethicalReading: true);
        }
    }

    private static byte[] Rewrite(byte[] source, string? password, bool unethicalReading)
    {
        using var reader = new PdfReader(new MemoryStream(source), CreateReaderProperties(password));
        if (unethicalReading)
            reader.SetUnethicalReading(true);

        using var output = new MemoryStream();
        // 스탬핑 모드의 기본값은 원본 암호화를 유지하지 않는 것이므로, 결과
        // 바이트는 평문 PDF가 된다.
        using (var writer = new PdfWriter(output, new WriterProperties()))
        using (var document = new PdfDocument(reader, writer, new StampingProperties()))
        {
        }
        return output.ToArray();
    }

    private static ReaderProperties CreateReaderProperties(string? password)
    {
        var properties = new ReaderProperties();
        byte[]? passwordBytes = ToPasswordBytes(password);
        if (passwordBytes != null)
            properties.SetPassword(passwordBytes);
        return properties;
    }

    private static byte[]? ToPasswordBytes(string? password) =>
        string.IsNullOrEmpty(password) ? null : Encoding.UTF8.GetBytes(password);
}

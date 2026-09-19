using System;
using System.Text;
using iText.Kernel.Pdf;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Models;

public enum PdfEncryptionAlgorithm { Aes256, Aes128 }

/// <summary>Output protection, kept separately from the plaintext editing snapshots.</summary>
public sealed record PdfProtectionSettings
{
    public bool Enabled { get; init; }
    public PdfEncryptionAlgorithm Algorithm { get; init; } = PdfEncryptionAlgorithm.Aes256;
    public bool EncryptMetadata { get; init; } = true;
    public bool RequireOpenPassword { get; init; }
    public bool RequireOwnerPassword { get; init; }
    public string? OpenPassword { get; init; }
    public string? OwnerPassword { get; init; }
    public int Permissions { get; init; } = PdfSecurityService.AllPermissions;

    public bool NeedsPasswords => Enabled &&
        (RequireOpenPassword && string.IsNullOrEmpty(OpenPassword) ||
         RequireOwnerPassword && string.IsNullOrEmpty(OwnerPassword));

    public int CryptoMode => (Algorithm == PdfEncryptionAlgorithm.Aes128
        ? EncryptionConstants.ENCRYPTION_AES_128 : EncryptionConstants.ENCRYPTION_AES_256)
        | (EncryptMetadata ? 0 : EncryptionConstants.DO_NOT_ENCRYPT_METADATA);

    public string? GetValidationError()
    {
        if (!Enabled) return null;
        if (!Enum.IsDefined(Algorithm)) return "지원하지 않는 암호화 방식입니다.";
        if (!RequireOpenPassword && !RequireOwnerPassword)
            return "열기 또는 권한 비밀번호를 하나 이상 설정해 주세요.";
        if (NeedsPasswords)
            return "기존 비밀번호를 복원할 수 없습니다. 보호 메뉴에서 비밀번호를 입력하거나 변경·삭제해 주세요.";
        if (RequireOpenPassword && RequireOwnerPassword && OpenPassword == OwnerPassword)
            return "열기 비밀번호와 권한 비밀번호는 서로 다르게 설정해 주세요.";
        int limit = Algorithm == PdfEncryptionAlgorithm.Aes128 ? 32 : 127;
        if (RequireOpenPassword && Encoding.UTF8.GetByteCount(OpenPassword!) > limit ||
            RequireOwnerPassword && Encoding.UTF8.GetByteCount(OwnerPassword!) > limit)
            return $"이 암호화 방식의 비밀번호는 UTF-8 기준 {limit}바이트 이하여야 합니다. 한글은 보통 글자당 3바이트입니다.";
        if (!RequireOwnerPassword && (Permissions & PdfSecurityService.AllPermissions) != PdfSecurityService.AllPermissions)
            return "사용 권한을 제한하려면 권한 비밀번호를 설정해 주세요.";
        return null;
    }

    // Never include credentials in logs or record diagnostics.
    public override string ToString() => $"PDF protection: {Enabled}, {Algorithm}";
}

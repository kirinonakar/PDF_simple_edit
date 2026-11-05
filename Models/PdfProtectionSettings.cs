using Loc = PDF_simple_edit.Services.LocalizationService;
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
        if (!Enum.IsDefined(Algorithm)) return Loc.Get("UnsupportedEncryptionAlgorithm");
        if (!RequireOpenPassword && !RequireOwnerPassword)
            return Loc.Get("SetAtLeastOneOpenOrPermissionsPassword");
        if (NeedsPasswords)
            return Loc.Get("TheExistingPasswordsCouldNotBeRecoveredEnterChange");
        if (RequireOpenPassword && RequireOwnerPassword && OpenPassword == OwnerPassword)
            return Loc.Get("UseDifferentPasswordsForOpeningTheDocumentAndChanging");
        int limit = Algorithm == PdfEncryptionAlgorithm.Aes128 ? 32 : 127;
        if (RequireOpenPassword && Encoding.UTF8.GetByteCount(OpenPassword!) > limit ||
            RequireOwnerPassword && Encoding.UTF8.GetByteCount(OwnerPassword!) > limit)
            return Loc.Format("PasswordsForThisEncryptionAlgorithmMustBeAtMost", limit);
        if (!RequireOwnerPassword && (Permissions & PdfSecurityService.AllPermissions) != PdfSecurityService.AllPermissions)
            return Loc.Get("SetAPermissionsPasswordToRestrictUsage");
        return null;
    }

    // Never include credentials in logs or record diagnostics.
    public override string ToString() => $"PDF protection: {Enabled}, {Algorithm}";
}

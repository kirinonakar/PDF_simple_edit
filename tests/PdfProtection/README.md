# PDF protection regression checks

Run from the repository root:

```powershell
dotnet run --project tests/PdfProtection/PdfProtection.csproj
```

Creates disposable PDFs and verifies AES-128/256, metadata encryption flags,
distinct open and owner authentication, unknown credentials, signed permission
flags, password changes and removal, content preservation, and protection of
merged, split, and exported output. Also checks password validation and prevents
silent UTF-8 truncation. No UI automation is used.

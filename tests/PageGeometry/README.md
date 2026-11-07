Run on Windows with the .NET 10 SDK:

```powershell
dotnet run --project tests/PageGeometry/PageGeometry.csproj -c Release -p:Platform=x64 --no-launch-profile
```

Exercises cropped pages at all four rotations, single/batch geometry edits,
proportional fit and canvas-only resizing, native annotation geometry, editable
paths, geometry-cache invalidation, exact byte/UI undo and redo, invalid-input
atomicity, native Windows PDF rendering, and save/reopen behavior. The program
prints the temporary directory containing its PDFs and rendered PNG fixtures.
It does not launch or automate the editor UI.

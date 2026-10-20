# Claude Code Instructions — Parsec Medical DICOM Viewer

> **Full Documentation:** See [AGENT.md](file:///F:/Rad/AGENT.md) for full architectural specs and [STRUCTURE.md](file:///F:/Rad/STRUCTURE.md) for directory tree details.

## Commands to Build, Run & Package
- **Build Release:** `dotnet build -c Release F:\Rad\OpenRadViewer\OpenRadViewer.csproj`
- **Build Debug:** `dotnet build F:\Rad\OpenRadViewer\OpenRadViewer.csproj`
- **Run Viewer:** `dotnet run --project F:\Rad\OpenRadViewer\OpenRadViewer.csproj`
- **Pack Installer:** `powershell -ExecutionPolicy Bypass -File F:\Rad\Installer\build_installer.ps1`
- **Output Setup:** `F:\Rad\Parsec_Setup_v1.1_Beta.exe`

## Tech Stack & Architecture Summary
- **Runtime:** .NET 8.0-windows, C# 12, WPF, `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`, `<Nullable>enable</Nullable>`.
- **DICOM:** `fo-dicom` 5.2.6 + `fo-dicom.Codecs` 5.16.7 (`NativeTranscoderManager` for JPEG Lossless 14/14SV1, JPEG 2000, JPEG-LS, RLE).
- **GPU 3D:** `Vortice.Direct3D11` / `D3DCompiler` 3.6.2 (HLSL Raymarching Volume Rendering).
- **MVVM:** `CommunityToolkit.Mvvm` 8.4.2.
- **Hierarchy:** `PatientModel` -> `StudyModel` -> `SeriesModel` -> `DicomSliceModel`.
- **Pixel Pipeline:** Raw pixel buffers (`ushort[]` / `short[]`) -> 65536-entry BGRA LUT (`PixelRenderer`) -> unmanaged pointer copy to `WriteableBitmap`.

## Key Files to Touch
1. **Root State & Viewport Grids:** [MainViewModel.cs](file:///F:/Rad/OpenRadViewer/ViewModels/MainViewModel.cs)
2. **2D Interactive Canvas & Mouse Tools:** [ViewportControl.xaml.cs](file:///F:/Rad/OpenRadViewer/Views/ViewportControl.xaml.cs)
3. **Viewport Tile State:** [ViewportViewModel.cs](file:///F:/Rad/OpenRadViewer/ViewModels/ViewportViewModel.cs)
4. **DICOM Scanning & Transcoding:** [DicomLoaderService.cs](file:///F:/Rad/OpenRadViewer/Core/Services/DicomLoaderService.cs)
5. **LUT Windowing & Rendering:** [PixelRenderer.cs](file:///F:/Rad/OpenRadViewer/Core/Services/PixelRenderer.cs)
6. **MPR 3D Reslicing (Ax/Cor/Sag/Obl):** [MprService.cs](file:///F:/Rad/OpenRadViewer/Core/Services/MprService.cs)
7. **Spatial Sync & 3D Cursor:** [SynchronizationService.cs](file:///F:/Rad/OpenRadViewer/Core/Services/SynchronizationService.cs)
8. **PACS Network Engine:** [PacsService.cs](file:///F:/Rad/OpenRadViewer/Core/Services/PacsService.cs)
9. **PACS C-STORE SCP & Filters:** [PacsModels.cs](file:///F:/Rad/OpenRadViewer/Core/Services/PacsModels.cs)
10. **GPU Volume Raymarching:** [D3D11VolumeRenderer.cs](file:///F:/Rad/OpenRadViewer/Core/Services/D3D11VolumeRenderer.cs)

## Strict Coding Guidelines
1. **Always include Header Docblock** on all new and modified files:
   ```csharp
   // @purpose: <What this file does>
   // @depends: <Dependencies>
   // @exports: <Public symbols>
   ```
2. **Include `// AI-NOTE:`** on non-obvious algorithms, DICOM tag workarounds, or hardware interop.
3. **Radiograph Integrity:** Single-frame projections (`CR`, `DX`, `RG`, `XA`) must NEVER be treated as scout slices or dimmed.
4. **PACS Compatibility:** Always accept `JPEGProcess14SV1` and standard transfer syntaxes in C-STORE SCP.
5. **Thread Safety:** Never block the WPF Dispatcher with disk I/O, network transfers, or MPR rendering.

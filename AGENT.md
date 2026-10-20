# Parsec Medical DICOM Viewer — AGENT.md
Universal AI Agent Architecture Guide & Reference Manual

> **Target Audience:** Antigravity, Claude Code, Cursor, Windsurf, Copilot, and human engineers.  
> **Repository Root:** `F:\Rad`  
> **Core Project:** `F:\Rad\OpenRadViewer\OpenRadViewer.csproj`

---

## 1. Project Overview

**Parsec Medical DICOM Viewer** is a high-performance, enterprise-grade, hardware-accelerated radiological desktop application built on **C# 12, .NET 8.0-windows, and WPF**. It provides professional multi-modality viewing (**CT, MRI, CR, DX, XA, US, PET/NM, SC**), real-time Multi-Planar Reconstruction (**MPR** Axial/Coronal/Sagittal/Oblique with MIP/MinIP), real-time GPU 3D Volume Rendering (**Direct3D 11 / HLSL raymarching**), local PACS server integration (**C-FIND, C-MOVE, pipelined C-STORE, C-STORE SCP listener**), and multi-phase AI lesion consultation (**Google Gemini & Anthropic Claude**).

The application is engineered for zero-latency clinical workflow: instantaneous streaming slice discovery, background transcoding of compressed DICOM files, unmanaged memory pointer rendering directly to WPF WriteableBitmaps, and robust crash-safe operation in clinical networks.

---

## 2. Quick Commands

All commands must be executed from `F:\Rad` using PowerShell or standard shell:

| Action | Command |
|---|---|
| **Restore Dependencies** | `dotnet restore "F:\Rad\OpenRadViewer\OpenRadViewer.csproj"` |
| **Build Debug** | `dotnet build "F:\Rad\OpenRadViewer\OpenRadViewer.csproj"` |
| **Build Release** | `dotnet build -c Release "F:\Rad\OpenRadViewer\OpenRadViewer.csproj"` |
| **Run Application** | `dotnet run --project "F:\Rad\OpenRadViewer\OpenRadViewer.csproj"` |
| **Run Executable Directly** | `& "F:\Rad\OpenRadViewer\bin\Release\net8.0-windows\Parsec.exe"` |
| **Build All-in-One Installer** | `powershell -ExecutionPolicy Bypass -File "F:\Rad\Installer\build_installer.ps1"` |
| **Installer Output Location** | `F:\Rad\Parsec_Setup_v1.1_Beta.exe` (approx. 236 MB self-contained) |

*Note: No external test runner is required; unit/synthetic tests use `DicomSampleGenerator.cs`.*

---

## 3. Tech Stack

- **Platform & Language:** .NET 8.0-windows (`net8.0-windows`), C# 12, WPF (Windows Presentation Foundation) with `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` and `<Nullable>enable</Nullable>`.
- **MVVM Framework:** `CommunityToolkit.Mvvm` (v8.4.2) — `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`.
- **DICOM Engine:**
  - `fo-dicom` (v5.2.6): DICOM file parsing, tag dictionary, dataset manipulation, and network communication (`DicomClient`, `DicomServerFactory`).
  - `fo-dicom.Codecs` (v5.16.7): Native C/C++ codecs wrapper (`Dicom.Native.dll`) using `NativeTranscoderManager` for JPEG Lossless (Process 14 / 14SV1), JPEG-LS, JPEG 2000, and RLE.
- **3D Hardware Rendering:**
  - `Vortice.Direct3D11` (v3.6.2), `Vortice.D3DCompiler` (v3.6.2), `Vortice.Mathematics` (v1.9.2): Direct3D 11 device, 3D texture binding, and HLSL raymarching shader compilation (`VolumeRenderingShader.hlsl`).
- **Network & Interop:**
  - Standard DICOM DIMSE: C-ECHO, C-FIND, C-MOVE, C-STORE SCU & SCP.
  - REST API: `System.Net.Http.HttpClient` for ParsecPACS HTTP synchronization (`http://127.0.0.1:8042/api`).
  - Win32 Interop: `dwmapi.dll` for dark mode titlebars, `user32.dll` for single-instance foreground activation, `System.IO.Pipes` for IPC.

---

## 4. Architecture & Data Flow

```
                      [ DICOM File / Folder / PACS ]
                                     |
                                     v
                       [ DicomLoaderService.cs ]
              (Streaming scan, tag extraction, multi-frame split)
                                     |
                                     v
                    [ Domain Hierarchy Models ]
           PatientModel -> StudyModel -> SeriesModel -> DicomSliceModel
                                     |
         +---------------------------+---------------------------+
         |                                                       |
         v                                                       v
  [ 2D Slice Flow ]                                       [ 3D MPR / Volume Flow ]
         |                                                       |
  [ LoadPixelData ]                                      [ MprService.cs ]
(Decompress if encapsulated)                         (Build continuous 3D scalar field,
         |                                             compute orthogonal/oblique cuts)
         v                                                       |
  [ PixelRenderer.cs ]                                           v
(LutHelper: 65,536 BGRA LUT,                            [ D3D11VolumeRenderer.cs ]
 unsafe pointer copy to WriteableBitmap)             (Upload to 3D GPU texture,
         |                                             HLSL raymarching with TF presets)
         v                                                       |
[ ViewportControl.xaml.cs ]                                      v
(WPF Image Canvas, HUD overlays,                     [ VolumeRenderingWindow.xaml.cs ]
 ROI measurements, 3D cursor)
```

### Key Data Structures:
1. **`PatientModel`** (`Core\Models\PatientAndStudyModels.cs`): Represents a patient (`Id`, `Name`, `BirthDate`, `Sex`, `Studies`).
2. **`StudyModel`**: Represents a clinical exam (`StudyInstanceUid`, `StudyDescription`, `StudyDate`, `AccessionNumber`, `Series`).
3. **`SeriesModel`**: Represents an acquisition sequence (`SeriesInstanceUid`, `SeriesDescription`, `Modality`, `Slices`, `InitialThumbnail`).
4. **`DicomSliceModel`** (`Core\Models\DicomSliceModel.cs`): Represents an individual slice/projection with geometry (`ImagePositionPatient`, `ImageOrientationPatient`, `PixelSpacingX/Y`, `SliceThickness`), default windowing (`DefaultWindowCenter/Width`), photometric interpretation, and raw pixel buffers (`RawPixelsUnsigned` or `RawPixelsSigned`).

---

## 5. Key Files Index

When starting a task, go directly to the relevant file:

| File | Relative Path | Purpose & Responsibilities |
|---|---|---|
| **`MainViewModel.cs`** | `OpenRadViewer\ViewModels\MainViewModel.cs` | Central application state: viewport grid matrices (1x1 to 4x4), active study list, scan orchestration, hanging protocols, sync modes. |
| **`ViewportControl.xaml.cs`** | `OpenRadViewer\Views\ViewportControl.xaml.cs` | Primary 2D viewport UI: mouse gesture handling (pan/zoom/windowing/drawing), HUD overlays, drag & drop, 3D localizer rendering. |
| **`ViewportViewModel.cs`** | `OpenRadViewer\ViewModels\ViewportViewModel.cs` | State of a single viewport tile: active series, current slice index, WL/WW, zoom, pan, Cine playback timer, ROI collection. |
| **`DicomLoaderService.cs`** | `OpenRadViewer\Core\Services\DicomLoaderService.cs` | DICOM streaming scanner, tag parser, multi-frame slicer, projection radiograph splitter, encapsulated transcoding (`DicomTranscoder`). |
| **`PixelRenderer.cs`** | `OpenRadViewer\Core\Services\PixelRenderer.cs` | High-performance 16-bit windowing and LUT rendering using unmanaged memory pointers directly to WPF `WriteableBitmap`. |
| **`MprService.cs`** | `OpenRadViewer\Core\Services\MprService.cs` | Multi-Planar Reconstruction engine: builds 3D volumes from slice series and extracts Axial, Coronal, Sagittal, and Oblique MIP/MinIP slices. |
| **`SynchronizationService.cs`** | `OpenRadViewer\Core\Services\SynchronizationService.cs` | 3D spatial coordinate projection between viewports: 3D reference cursor, scout lines, and spatial slice matching. |
| **`PacsService.cs`** | `OpenRadViewer\Core\Services\PacsService.cs` | Full PACS client engine: C-ECHO, C-FIND, C-MOVE, pipelined C-STORE SCU, background auto-refresh, and C-STORE SCP listener. |
| **`PacsModels.cs`** | `OpenRadViewer\Core\Services\PacsModels.cs` | PACS data models, filter criteria (`Matches`), server configs, and the `PacsCStoreScpService` presentation context negotiator. |
| **`D3D11VolumeRenderer.cs`** | `OpenRadViewer\Core\Services\D3D11VolumeRenderer.cs` | Direct3D 11 GPU raymarching renderer: 3D textures, transfer function ramps (Bone, Angio, Skin, MIP), camera transforms, clip planes. |
| **`AiService.cs`** | `OpenRadViewer\Core\Services\AiService.cs` | Multi-modal radiological AI consultation: handles multi-phase lesion crops, structured prompts, and Gemini / Claude API calls. |
| **`build_installer.ps1`** | `Installer\build_installer.ps1` | Automated packaging script producing the standalone `Parsec_Setup_v1.1_Beta.exe` installer with registry integration. |

---

## 6. Coding Guidelines & AI Constraints

1. **Header Docblocks Mandatory:** Every new or edited source file MUST start with the standard 3-tag docblock:
   ```csharp
   // @purpose: <Precise description of what this file does>
   // @depends: <Key assemblies, modules, or services relied on>
   // @exports: <Key public classes, interfaces, or enums provided>
   ```
2. **AI Notes on Complex Logic:** Add `// AI-NOTE: ...` comments before tricky formulas, edge cases (e.g., projection radiography sharing `SeriesInstanceUID`, DICOM VOI LUT inversion, Direct3D resource mapping).
3. **No Dynamic / Weak Typing:** Never use `dynamic` or unnecessary `object` casts. Use strict typed models (`DicomSliceModel`, `SeriesModel`, `PacsStudyResult`).
4. **Preserve Modality & PACS Conformance:**
   - Radiographs (`CR`, `DX`, `RG`, `XA`) have 1 slice and must NOT be dimmed as scouts.
   - PACS C-STORE SCP must always accept `JPEGProcess14SV1`, `JPEGProcess14`, `JPEG2000Lossless`, `JPEG2000Lossy`, `JPEGLSLossless`, `RLELossless`, and Little/Big Endian.
   - Encapsulated datasets (`InternalTransferSyntax.IsEncapsulated`) must be decompressed via `DicomTranscoder` before memory block copy.
5. **Thread Safety & UI Responsiveness:**
   - CPU-heavy loops (pixel transcoding, MPR matrix slicing) must execute on background threads via `Parallel.ForEach` or `Task.Run`.
   - Dispatcher updates must be throttled or batched to avoid UI stutter.

---

## 7. How to Add New Features

### A. Adding a New Viewport Measurement Tool
1. In `Core\Config\MouseBindingConfig.cs`: Add the action to `MouseToolAction` enum.
2. In `Core\Models\Measurement.cs`: Add the type to `MeasurementType` and implement geometry calculation in `MeasurementItem`.
3. In `Views\ViewportControl.xaml.cs`: Handle the mouse events (`OnMouseDown`, `OnMouseMove`, `OnMouseUp`) for the new action and render the SVG/drawing shapes on the canvas.
4. In `Views\MainWindow.xaml.cs`: Bind the toolbar icon or shortcut to activate the new tool.

### B. Adding a New PACS Functionality or Modality Filter
1. In `Core\Services\PacsModels.cs`: Update `PacsSearchFilter.Matches` to accommodate the modality logic.
2. In `Core\Services\PacsService.cs`: Add or update the DIMSE request (C-FIND, C-MOVE, or C-STORE).
3. In `Views\PacsSearchWindow.xaml.cs`: Wire the UI control/combobox and trigger the asynchronous query.

### C. Adding a New Clinical Calculator or Reference Tool
1. Create model in `Core\Models\` or service logic in `Core\Services\`.
2. Create XAML Window/Dialog in `Views\`. Call `DarkWindowHelper.ApplyDarkTitleBar(this)` in constructor.
3. Hook into the `ExtraTools` menu in `Views\MainWindow.xaml` and `MainWindow.xaml.cs`.

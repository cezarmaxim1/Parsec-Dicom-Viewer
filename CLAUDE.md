# Parsec Medical DICOM Viewer — CLAUDE.md

Comprehensive Developer, Architecture & AI Agent Reference Manual

Version: **v1.3.2 Beta (1.3.2-beta)** | Target Framework: **.NET 8.0-windows / C# 12 / WPF**

---

## 1. Project Mission & Overview

**Parsec** is an ultra-fast, diagnostic-grade medical workstation desktop application built for clinical radiologists and hospital emergency departments. It is maintained in two distinct editions: **Aplicația Locală (Local Edition)** in `F:\Rad` for private clinical workflow, and **Aplicația Publică (Public / GitHub Edition)** on `origin/main` (`https://github.com/cezarmaxim1/Parsec-Dicom-Viewer.git`). It combines:

- **Instantaneous Multi-Modality 2D DICOM Viewing** (CT, MRI, CR, DX, XA, US, PET/NM, SC) with 65,536-entry LUT windowing and unmanaged pointer blitting.

- **Real-Time Multi-Planar Reconstruction (3D MPR)**: Simultaneous 3-plane orthogonal workspace (Axial, Coronal, Sagittal, Oblique) with interactive crosshairs and MIP (Maximum Intensity Projection), MinIP (Minimum Intensity Projection), and AVG (Average) slab rendering (adjustable 2–50 mm).

- **GPU 3D Volume Rendering (3D VR)**: Direct3D 11 / HLSL raymarching using `Vortice.Direct3D11` with transfer function presets for Bones, Angio, Skin, MIP, and Airways.

- **Local PACS DIMSE Engine**: Standard DICOM C-FIND, C-MOVE, C-GET, and high-performance background C-STORE SCP server (plus optional HTTP ZIP / notes sync with ParsecPACS on port 8042).

- **AI Clinical Reporting & Voice Dictation**:

  - Direct microphone voice dictation powered by Google Gemini (default `gemini-3.8-flash`), with local Whisper as the fallback.

  - One-touch structured Romanian medical report formatting (F5) and configurable Autoformat on dictation completion (F4).

  - Conversational voice AI refinement (F6).

  - Instant Microsoft Word (.docx) export with automatic patient demographics and Emergency Department header (`antet_ct_urgente.docx`).

  - Persistent custom rules (`ai_custom_rules.txt`) for formatting and clinical styling.

- **AI Lesion Consultation**: Multi-phase second-opinion chat with Gemini or Claude (`AiService.cs`, `AiConsultationWindow`).

---

## 2. Essential Commands & Developer Workflows

Execute all commands from the repository root: `F:\Rad`

### Build & Run

```powershell

# Restore NuGet dependencies

dotnet restore OpenRadViewer/OpenRadViewer.csproj

# Fast Debug Build

dotnet build OpenRadViewer/OpenRadViewer.csproj

# Optimized Release Build

dotnet build -c Release OpenRadViewer/OpenRadViewer.csproj

# Run Viewer directly from project

dotnet run --project OpenRadViewer/OpenRadViewer.csproj

# Run built Release executable directly

& "F:\Rad\OpenRadViewer\bin\Release\net8.0-windows\Parsec.exe"

```

### Process Management

Before building or running, always ensure previous instances of `Parsec.exe` are stopped so binaries are not file-locked:

```powershell

Stop-Process -Name "Parsec" -Force -ErrorAction SilentlyContinue

```

### Installer Packaging

The complete stand-alone, self-contained desktop installer (with bundled .NET runtime and native transcoder codecs) is compiled using Inno Setup via PowerShell:

```powershell

powershell -ExecutionPolicy Bypass -File "F:\Rad\Installer\build_installer.ps1"

# Outputs: F:\Rad\Parsec_Setup_v1.3.2_Beta.exe

```

Build the installer only when the user asks for one, never as a side effect of other work.

- The installer copies `OpenRadViewer\Assets\*` into the app. Those files are local only (git-ignored), so check them for names before a build.
- It never contains `pacs_config.json`, `gemini_key.txt`, the database, or the PACS cache, which live in `%AppData%\Parsec` or next to the built exe.
- To let another PC on the same network download it, copy it to `Dist\` and start the `parsec-download` entry in `.claude\launch.json`. It serves `Dist\` on port 8766 at the laptop's Wi-Fi address, not `localhost`. Port 8765 belongs to another project.

### Running the App After Changes

The user starts Parsec from a desktop shortcut to `OpenRadViewer\bin\Debug\net8.0-windows\Parsec.exe`. After a code change, ask the user to close Parsec (or close it if they allow), rebuild Debug, and start it again. A Release build alone does not update what they run.

### Verifying Changes

There is no automated test project. Verify changes with a Release build (`0 Error(s)`) and by running the app.

`OpenRadViewer\Assets\*` is git-ignored except `.gitkeep` and `cfr_header_template.docx`, so classification images, reference texts, and `antet_ct_urgente.docx` exist only on the local machine.

---

## 3. Technology Stack & Key Dependencies

- **Runtime / Language:** .NET 8.0-windows (`net8.0-windows`), C# 12, WPF.

  - Project configuration: `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`, `<Nullable>enable</Nullable>`.

- **MVVM Architecture:** `CommunityToolkit.Mvvm` (v8.4.2)

  - `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`.

- **DICOM Library & Codecs:**

  - `fo-dicom` (v5.2.6): DICOM dataset reading/writing, tags dictionary, DIMSE networking.

  - `fo-dicom.Codecs` (v5.16.7): Native C++ transcoders (`Dicom.Native.dll`) via `NativeTranscoderManager` supporting JPEG Lossless (Process 14 / 14SV1), JPEG-LS, JPEG 2000, and RLE.

- **Hardware-Accelerated 3D Volume Rendering:**

  - `Vortice.Direct3D11` (v3.6.2), `Vortice.D3DCompiler` (v3.6.2), `Vortice.Mathematics` (v1.9.2).

  - Custom HLSL raymarching shader (`VolumeRenderingShader.hlsl`).

- **Audio & Voice Processing:**

  - Windows `winmm.dll` (MCI `mciSendString`): Microphone capture at 16 kHz, 16-bit mono PCM.

  - Google Gemini REST API (`https://generativelanguage.googleapis.com/v1beta/models/...`).

  - `Whisper.net` and `Whisper.net.Runtime` (v1.9.1): Local transcription fallback.

- **Archives:**

  - `SharpCompress` (v0.50.7): Opening `.zip`, `.rar`, `.7z`, `.tar`, and `.gz` medical archives (`ArchiveExtractionService.cs`).

- **Document Generation:**

  - Custom OpenXML packaging (`DocxExportService.cs`, using the `cfr_header_template.docx` and `antet_ct_urgente.docx` templates).

---

## 4. Architectural Domain & Data Flow

```

                         [ DICOM Sources ]

              (Local Folder / DIMSE PACS)

                                    │

                                    ▼

                         [ DicomLoaderService.cs ]

              Streaming tag extraction, slice geometry parsing,

                    multi-frame splitting, transcoder init

                                    │

                                    ▼

                        [ Domain Model Hierarchy ]

               PatientModel ──► StudyModel ──► SeriesModel ──► DicomSliceModel

                                    │

        ┌───────────────────────────┴───────────────────────────┐

        ▼                                                       ▼

 [ 2D Slice Pipeline ]                                   [ 3D MPR / VR Pipeline ]

        │                                                       │

 [ DicomSliceModel.LoadPixelData() ]                     [ MprService.cs ]

 Raw 16-bit scalar buffers (`ushort[]`/`short[]`)         3D scalar matrix interpolation,

        │                                                MIP/MinIP/AVG slab projections

        ▼                                                       │

 [ PixelRenderer.cs ]                                           ▼

 65,536-entry BGRA LUT mapping                           [ D3D11VolumeRenderer.cs ]

 Unsafe pointer copy to WPF WriteableBitmap              3D Texture binding, GPU raymarching

        │                                                       │

        ▼                                                       ▼

 [ ViewportControl.xaml ]                                [ VolumeRenderingWindow.xaml ]

 Canvas rendering, HUD overlays,                         Interactive rotation, TF presets,

 ROIs, 3D crosshairs, vertebral labels                   clipping planes

```

### Domain Data Hierarchy

1. **`PatientModel`** (`Core\Models\PatientAndStudyModels.cs`): Patient ID, Name, Birth Date, Sex, collection of Studies.

2. **`StudyModel`**: Study Instance UID, Date/Time, Description, Modality, Accession Number, series collection.

3. **`SeriesModel`**: Series Instance UID, Description, Series Number, Modality, slice collection, initial thumbnail bitmap.

4. **`DicomSliceModel`** (`Core\Models\DicomSliceModel.cs`): Geometry (`ImagePositionPatient`, `ImageOrientationPatient`, `PixelSpacing`, `SliceThickness`), Default Window/Level, Photometric Interpretation, signed/unsigned pixel buffers.

5. **`ViewportViewModel`** (`ViewModels\ViewportViewModel.cs`): State of an individual viewport tile: displayed series, active slice index, custom WL/WW, zoom factor, pan offset, cine state, ROI collection.

---

## 5. PACS Architecture

### Local PACS (Standard DIMSE C-FIND / C-MOVE / C-GET / C-STORE)

- Implemented in `OpenRadViewer\Core\Services\PacsService.cs`.

- Provides asynchronous `C-ECHO` pinging, `C-FIND` study queries with modality, date, and patient filters.

- `C-MOVE` requests trigger incoming transfers to Parsec's background C-STORE SCP server (default port `46001`, default AE Title `AE_REZIDENTI@@`, both configurable).

- Slices stream in real-time into the viewer without waiting for the entire series to download.

### Notes Sync with ParsecPACS (`ParsecPacsSyncService.cs`, `DatabaseService.cs`)

- Notes are read and saved through `GET` and `POST /api/studies/{uid}/details` on port 8042. The newest edit wins, by `PatientCommentsUpdatedAt` on the local record and `notes_updated_at` on the server.
- `database_index.json` can be written by ParsecPACS, so every change goes through `DatabaseService.UpdateRecords`: re-read, apply only the changed fields, replace atomically. It writes nothing if the file cannot be read. Unknown fields are kept with `[JsonExtensionData]`.
- C-FIND returns a note without a time, so it can fill a record with no edit time but never replaces a timed note. A difference triggers `SyncRecordNoteAsync`.
- A local note saved while the server is down is sent by `SyncRecordNoteAsync` when the Database window opens or the PACS list refreshes. This does not rely on ParsecPACS reading the file, because the server may run on another computer.
- `local_notes.json` holds notes for studies with no local record that are not in PACS. They are shown instead of the PACS note and are sent only when the user saves them again. Entries from before this sync are never uploaded automatically.

### PACS Search Window (`PacsSearchWindow`)

- **Search box:** digits only (spaces ignored) search the Patient ID. A full 13-digit CNP is matched exactly and a shorter one as a prefix (`276*`), because many PACS ignore a leading `*` on Patient ID.
- **Names:** any other text searches the name. Each word must appear, in any order: `popescu io` becomes `*popescu*io*` in C-FIND, matching the stored `POPESCU^IOANA`.
- **Size column:** shows the size of the local copy only. C-FIND does not report a study size.
- **Right-click menu:** "Delete from Local Database" appears only for studies with a local copy. It removes the copy, and the study stays in PACS.

---

## 6. AI Clinical Reporting & Voice Dictation Suite

Located in `OpenRadViewer\Core\Services\SpeechRecognitionService.cs` and `MainWindow.xaml`:

### Voice Dictation (Key F4)

- Captures microphone input via Windows MCI (`winmm.dll`, 16 kHz, 16-bit mono PCM).

- Sends the audio to the selected Gemini model (default `gemini-3.8-flash`) with radiological Romanian prompting.

- Falls back through `gemini-3.6-flash`, `gemini-3.5-flash`, and `gemini-3.5-flash-lite`. Overloaded models are skipped for 15 minutes (`overloaded_models.json`), and timings are logged to `ai_timing.log`.

- Uses local Whisper when no Gemini key is set or Gemini fails.

- Saved model names containing `2.5`, `2.0`, or `1.5` are migrated to `gemini-3.8-flash` on load.

- Inserts dictated text seamlessly at the current cursor position in the report editor.

- **Autoformat Mode:** When enabled in Voice Settings, stopping voice dictation automatically formats the complete report into structured sections without needing to press F5.

### AI Report Formatter (Key F5)

- Transforms unstructured or shorthand radiological dictation into a standard structured report:

  - *Examinare:* (Exam type & contrast details)

  - *Date clinice:* (Clinical history)

  - *Protocol tehnic:* (Slice thickness, planes, contrast phases)

  - *Descriere:* (Organ-by-organ anatomical findings)

  - *Concluzii:* (Numbered diagnostic synthesis)

- Automatically disabled/grayed out with explanatory tooltip when **Autoformat AI** is active.

### Conversational Voice Refine (Key F6)

- Allows hands-free voice commands to edit or refine the report (e.g., *"corectează diametrul chistului la 15 mm"*, *"adaugă la concluzii fără semne de recidivă"*).

- Applies changes intelligently to the existing text while maintaining an undo history stack.

### Report Header Fields (`ClinicalProtocolsService.cs`)

- **Exam name:** `ResolveExaminationName` keeps the modality of the selected protocol, so an IRM protocol is never reported as CT. `AppendLaterality` adds `STANG` or `DREPT` to paired regions (knee, shoulder, hip, and others) when the study or series descriptions name one side.
- **Contrast:** `ResolveContrast` decides "Contrast: Da / Nu".
  - A contrast marker in the study or any series gives "Da" (`+C`, `CIV`, `CONTRAST`, `Gd`, `POST`, arterial, venous, portal, delayed, angio). Markers are matched as whole tokens.
  - Otherwise, a study described as native or without contrast gives "Nu", even if the protocol defaults to "Da".
  - Otherwise, the protocol default is used.
- **Age from CNP:** first digits 7, 8, and 9 carry no century, so the most recent year that is not in the future is used.

### Word (.docx) & Emergency Export

- **Save as .docx (Ctrl+S):** Exports a formatted Microsoft Word document with patient name, CNP, date, and exam description.
  - *Local Edition:* Uses the CFR hospital header (`cfr_header_template.docx`).
  - *Public (GitHub) Edition:* Uses an empty header (`antet gol` — all hospital logos, lines, and header text removed), keeping the body and demographic fields intact.

- **Save as URG.docx:** Uses the Emergency Department template (`antet_ct_urgente.docx`).
  - *Local Edition:* Default filename `URG CM <nume_pacient>.docx`; bottom table includes the two local doctor names (`Medic examinator` and `Medic rezident` names).
  - *Public (GitHub) Edition:* Default filename `URG <nume_pacient>.docx` (no `CM`); bottom table keeps `Medic rezident:` and `Medic examinator:` with blank space underneath (no doctor names).

### Custom Persistent Rules (`ai_custom_rules.txt`)

- Saved in `%AppData%\Parsec\ai_custom_rules.txt` and edited in the Voice Settings dialog.

- Radiologists can persist specific personal instructions (e.g., phrasing preferences, specific classification mentions, measurement formats), which are automatically appended to all AI system prompts.

---

## 7. Toolbar & User Interface Layout

The main toolbar doubles as the title bar. It is one row of capsule groups, which wraps to a second row on narrow windows:

```
(Logo) ( Database  PACS  Open ) ( Grid ▾  Split ▾  Sync ▾ )
( Crosshair  Stack ▾  Pan  Zoom  W/L  Windows ▾ ) ( 3D MPR  Plane ▾  3D VR )
( Tools ▾  Extras ▾  [Report] ) ( Rotate ▾  Cine  Export ) ( Close ▾  Fullscreen  Patient  About )
      ...      – □ ✕
```

- **Default Launch State:** Application launches with the Main Menu Bar hidden by default (`ShowMenuBar = false`) for maximal vertical screen estate. When shown, the menu sits below the toolbar.
- **Toolbar Right-Click Context Menu:** Right-clicking the toolbar outside its buttons opens the View dropdown menu (toggles for Main Menu Bar `Ctrl+Alt+F5`, Series Preview Bar, Toolbars & Status Bar, Scout Lines, Coronal/Sagittal planes, 3D MPR, 3D VR, Invert Scrolling, and Full Screen Mode).
- **Accent Color:** Parsec orange `#F5AF28` is the brand color: active viewport border, Report, Sync in Auto mode, and 3D MPR while active. Other toolbar content stays neutral.
- **No Selected State for Mouse Tools:** Left, right, and hold-wheel can each be bound to a different tool at once, so tool buttons have no selected background. `ToolBindingIndicator` dots alone show the bindings (left `#F5AF28`, right `#40C0FF`, hold-wheel `#80E880`).

### Custom Title Bar
Used by `MainWindow`, `DatabaseWindow`, and `PacsSearchWindow`. Their toolbars replace the native title bar:
- `CustomTitleBar.Attach(window, RootGrid, dragArea)` applies `WindowChrome` with `CaptionHeight="0"`, so toolbar content stays normal WPF. The drag area starts a native caption drag (`WM_NCLBUTTONDOWN`) to keep Aero Snap and drag-to-restore, and double-click maximizes or restores.
- It insets the root element while maximized, because a maximized chrome window extends past the screen edge. It ignores mouse events routed from popups.
- `WindowControlsBar` holds the minimize, maximize/restore, and close buttons.
- `MainWindow.ToggleFullScreen` must leave `Maximized` before setting `WindowStyle.None`, or the taskbar stays visible.
- The Windows 11 Snap Layouts flyout on the maximize button is not available. Hiding the main toolbar (`Ctrl+Alt+F3`) also hides its window controls.

### Viewport Panels and Measurements
- **Closing a panel:** closing a panel moves the remaining series to the front and empties every cell after them, so no series appears twice. The grid adapts to the count: 5 → 3 over 2, 4 → 2×2, 3 → side by side.
- **3D crosshair:** shown only while its mouse button is held, with the pointer hidden. It clears on release, on lost capture, and on any move with no button pressed.
- **Measurement style:**
  - ROI: thin Parsec-orange outline, corner brackets and a centre move grip when selected, and an outlined label joined by a thin dotted leader.
  - Length line: white, turning orange with round handles when selected, with a dark pill label.
- **Measurement editing:** the handles of the selected measurement resize it even while a drawing tool is active. Clicking an outline or the centre grip selects and moves it instead of starting a new one.
- **Menu toggles:** a checkable `MenuItem` with a `Click` handler must bind `IsChecked` with `Mode=OneWay` and must not set `IsCheckable`, or each click toggles twice.

### Local Database Window
- **Size column:** stored as `SizeBytes`. Older records are measured once by `BackfillSizes`.
- **Del key:** handled in `PreviewKeyDown`, because the DataGrid consumes Delete and Enter before a `KeyDown` handler sees them.
- **Delete:** runs off the UI thread and reports a failure instead of doing nothing. If the index cannot be updated, no files are deleted.
- **Clear Database:** only on the toolbar, not in the row menu.

### Shared Styles (`App.xaml`)
- `ToolbarButton`: Borderless, 32 px high, gray hover. `PanelButton`: The same on a `#242424` fill, for buttons outside capsules. `ToolbarCapsule`: Group background `#242424`, radius 10.
- `ToolIcon` + `Icon*` geometries: Outline icons on a 24×24 grid scaled to 70%, from Tabler Icons v3.19.0 (MIT). Their color follows the parent button's `Foreground`; outside a button, set `Stroke` locally.
- `ToolbarArrowButton`: Compact dropdown arrow nested inside a button. Its click handler must set `e.Handled = true`.

### Colors in the Report, Database, and PACS Views
- Orange marks primary actions: Format AI, Search PACS, Open. Selected table rows use the tint `#3B2C0D`.
- Other colors carry meaning only: red tints for recording and Stop, red text for Delete and Clear Database, the warm tint on Save as URG.docx, and modality badges.
- Do not use emoji in buttons. Use `Icon*` geometries.

### Key Toolbar Elements:
1. **Window / Level (W/L):**
   - **`BtnWindowTool`**: Icon-only button (contrast circle + tool binding indicator). Left/Right/Wheel click binds the W/L drag tool to mouse buttons.
   - **`BtnWindowMenu` (`Windows ▾`)**: Dedicated presets dropdown button:
     - Presets `0` to `9` (Default, Full Dynamic, Abdomen CT, Angio CT, Bone CT, Brain CT, Chest CT, Lung CT, Liver CT, Stroke CT).
     - Add Current Window as Preset.
     - Custom Window / Level & Presets Dialog (`Ctrl+W`).
     - Negative / Invert (`F11`).
2. **Clinical Report Button (`Report`):**
   - Orange text and icon on tint `#3B2C0D`, the only tinted button on the toolbar.
   - Single direct action: Left-Click toggles the Clinical Report side panel. No dropdown arrow or submenu.
3. **Plane Selector (`BtnMprMenu`, `Plane ▾`):**
   - Stays visible outside 3D MPR, because `SwitchActivePlane` reconstructs a coronal or sagittal plane in a normal viewport.
4. **Rotate Split Button (`BtnTransformMenu`):**
   - Click rotates 90° clockwise. The arrow opens Rotate CCW/CW and Flip Horizontal/Vertical.
5. **Measurement Tools Dropdown (`Tools ▾`):**
   - Length Ruler (`L`), Ellipse ROI with Area & Mean HU (`E`), Rectangle ROI (`U`), Angle (`A`), Insall-Salvati Ratio, Arrow (`Y`), Pencil (`P`), Text Annotation (`T`).
6. **Extras Dropdown (`Extras ▾`):**
   - Vertebral Body Labeling (Spine Lumbar L5–L1, Thoracic T12–T1, Cervical C7–C1).
   - Adrenal CT Washout Calculator.
   - Classification Scales:
     - Pfirrmann (Lumbar intervertebral disc degeneration)
     - Scheltens / MTA (Medial temporal lobe atrophy for Alzheimer's / dementia)
     - Fazekas (White matter hyperintensities)
     - Bosniak (Renal cystic masses)
     - LI-RADS (Hepatocellular carcinoma)
     - Ischemic Infarct MRI Evolution Table (DWI / ADC / T2 progression)
7. **Button Geometry:**
   - Single-icon buttons are `Width="32"`, `Padding="0"`. Tool indicator grids are 26×26 with `ToolBindingIndicator` in the top-left corner.
   - Icon + arrow buttons (`BtnGridPicker`, `BtnBrowse`, `BtnTransformMenu`, `BtnCloseMenu`) are `Width="40"`.

---

## 8. Master Keyboard Shortcuts & Gestures Reference

| Shortcut | Context | Action |

|---|---|---|

| **F4** | Global | Toggle Voice Dictation (opens report panel automatically if closed) |

| **F5** | Global | Format dictated text into structured radiological report with AI |

| **F6** | Global | Toggle Conversational Voice Refine (interactive hands-free editing) |

| **Ctrl + S** | Report Panel | Save complete report as Microsoft Word (.docx) document |

| **Ctrl + W** | Global | Open Custom Window / Level & Presets Dialog |

| **F11** | Viewport | Toggle Invert Window / Level (Negative mode) |

| **0 – 9** | Viewport | Apply Window Presets (0: Default, 1: Dynamic, 2: Abdomen, 3: Angio, 4: Bone, 5: Brain, 6: Chest, 7: Lung, 8: Liver, 9: Stroke) |

| **Alt + M** | Global | Toggle 3D Multi-Planar Reconstruction (3D MPR) mode |

| **Ctrl + M** | Global | Maximize / restore the active panel |

| **Ctrl + Alt + F5** / **Ctrl + Alt + M** | Global | Toggle Main Menu Bar |

| **Ctrl + Alt + F2** / **Ctrl + Alt + F3** | Global | Toggle Series Preview Bar / Toolbars |

| **Ctrl + D** / **Ctrl + P** | Global | Open Local Database / PACS Search |

| **Ctrl + O** / **Ctrl + Shift + O** | Global | Open files / Scan folder |

| **Tab** / **Shift + Tab** | Global | Activate next / previous panel |

| **X**, then **1 – 9** | Global | Quick grid layouts |

| **Q, B, W, M, Z** | Viewport | Left-mouse tool: 3D Cursor, Browse, Window/Level, Pan, Zoom |

| **L, E, U, A, Y, P, T** | Viewport | Left-mouse tool: Length, Ellipse, Rectangle, Angle, Arrow, Pencil, Text |

| **Alt + 1 / 2 / 3** | MPR | Switch Orthogonal Plane (1: Coronal, 2: Sagittal, 3: Axial) |

| **H** | MPR | Toggle MPR Reference Axes / Crosshairs visibility |

| **Ctrl + Shift + V** | Global | Open Direct3D 11 GPU 3D Volume Rendering (3D VR) window |

| **Ctrl + F12** | Viewport | Toggle Scout Lines / 3D Cross-Reference Lines |

| **Space** | Viewport | Play / Stop Cine Loop playback |

| **Up / Down** | Viewport | Previous / next image |

| **Page Up / Page Down** | Viewport | Move 10 images back / forward |

| **Home / End** | Viewport | First / last image |

| **Ctrl + [ / ]** | Viewport | Rotate image 90° CCW / CW |

| **Ctrl + Shift + [ / ]** | Viewport | Flip image horizontally / vertically |

| **Ctrl + 0 – 4** | Viewport | Reset transform / zoom 1x, 2x, 4x, 8x |

| **Ctrl + + / -** | Viewport | Zoom in / out |

| **Ctrl + C** | Viewport | Copy image to clipboard |

| **Ctrl + E** | Viewport | Export current image / series |

| **Esc** / **Shift + Esc** | Viewport | Cancel the current tool, or close the active panel / close all panels |

| **Ctrl + F4** | Global | Close active viewport panel |

| **Shift + F4** | Global | Close all open viewport panels |

| **F** | Global | Toggle Fullscreen mode |

| **Del** | Viewport | Delete the selected measurement / ROI or vertebra label |

| **Ctrl + Shift + D** | Viewport | Duplicate active measurement ROI to all other series |

---

## 9. Critical Invariants & Rules for Coding Assistants

1. **Header Docblocks:** Every new or edited source file must preserve or include the header:

   ```csharp

   // @purpose: <Description of file role>

   // @depends: <Dependencies>

   // @exports: <Public classes/interfaces>

   ```

2. **Single-Frame Radiograph Projection Protection:**

   - Radiographs (`CR`, `DX`, `RG`, `XA`) must **NEVER** be treated as scout slices or dimmed by scout overlays. They are primary diagnostic planar acquisitions.

3. **Thread Safety & UI Dispatcher:**

   - Never perform network transfers, disk extraction, DICOM parsing, or GPU updates on the WPF Dispatcher thread. Use `Task.Run()`, `async/await`, and report progress through `IProgress<T>`.

4. **Memory & Buffer Safety:**

   - LUT and pixel copies in `PixelRenderer.cs` use `unsafe` pointers for 60+ FPS playback. Always wrap buffer operations in appropriate bounds checks to avoid memory access violations.

5. **No Installer Builds in Routine Tasks:**

   - Never build `Parsec.Setup.csproj` or run Inno Setup unless the user explicitly requests an installer build. Focus on compiling and testing `OpenRadViewer.csproj`.

6. **Keep the Docs Current:**

   - When adding a service or major view, update `STRUCTURE.md`.

7. **No Patient Data in the Repository:**

   - Never put real patient names, CNPs, or study data in source, docs, or commit messages. Scratch scripts belong outside the repository.
   - Use made-up examples in comments and tests, such as `POPESCU^IOANA` or `1800101123456`, never names or CNPs from the user's screenshots.
   - `Assets\URGENTE CT.txt` and `Assets\modele_rezultate.txt` were cleaned of doctor and patient names and FO numbers on 2026-11-14.
   - `antet_ct_urgente.docx` still holds the names of two doctors in its footer, by the user's choice.
   - In these texts, `DR` can also mean "drept" (for example `GENUNCHI DR`), so do not remove it blindly.

8. **No Credentials in Code:**

   - No accounts, passwords, or API keys as defaults. API keys are entered in the app and stored in `%AppData%\Parsec` (`gemini_key.txt`).
   - The repository has a GitHub remote, so a push publishes everything in history.

9. **Dual-Edition Architecture: Local Application vs. Public (GitHub) Application:**

   - **Explicit User Request Only:** **NEVER** push to GitHub (`origin/main`) or create a GitHub Release unless the user explicitly instructs you to do so. Local `F:\Rad` (`main` branch) always remains on the **Local Edition**.
   - **Exact 4 Differences in the Public (GitHub) Edition:**
     1. **Save as URG (`.docx`) Signatures (`Assets\antet_ct_urgente.docx` & `DocxExportService.cs`):** Remove the two doctor names and resident title from the bottom signature table, leaving `Medic rezident:` and `Medic examinator:` with blank space underneath.
     2. **Save as URG (`.docx`) Default Filename (`Views\MainWindow.xaml.cs`):** Use `URG <nume_pacient>.docx` instead of `URG CM <nume_pacient>.docx`.
     3. **Save as `.docx` Header (`Assets\cfr_header_template.docx` & `DocxExportService.cs`):** Remove the entire CFR hospital header (all 3 logos, separator lines, and `MINISTERUL TRANSPORTURILOR ȘI INFRASTRUCTURII SPITALUL CLINIC CĂI FERATE IAȘI...`), leaving an empty header (`antet gol`) while preserving the body text and all patient/study demographic fields (`Nume`, `Prenume`, `CNP`, `Vârsta`, `Zona examinată`, `Contrast`, `DLP`, `DATA`, `MEDIC EXAMINATOR`).
     4. **About Parsec Self-Updater (`Views\AboutWindow.xaml/.cs`, `Core\Services\AppUpdateService.cs`, `version.json`):** Include a **Check for Updates** button in `AboutWindow` that checks GitHub (`cezarmaxim1/Parsec-Dicom-Viewer`) via `version.json` / GitHub Releases and downloads/installs the newer setup if accepted by the user. About 5 seconds after start, `AppUpdateService.CheckOnStartupAsync` checks once and offers Install, Later (asks again at the next start), or Don't remind me (turns the startup check off; a checkbox in About turns it back on). The choice is saved in `%AppData%\Parsec\update_prefs.json`. The startup call in `MainWindow.OnWindowLoaded` exists only in the public edition.
   - **Git Isolation Procedure:** When publishing to GitHub, apply the 4 Public Edition changes, build the public installer if requested, create a **single squashed commit** parented directly on `origin/main` (e.g. via `git commit-tree`) so intermediate local commits are never exposed, push to `origin/main`, upload the public installer to GitHub Releases, and immediately restore `F:\Rad` to the Local Edition.

---

## 10. Known Issues

Found by reading the code on 2026-11-01. None of these has been reproduced at runtime.

### Reliability

- `PixelRenderer.RenderThumbnail` does not check buffer lengths, unlike `RenderToBitmap`. `RenderToBitmap` also leaves the previous slice's pixels in a reused bitmap if a buffer is short.

- `PixelRenderer.RenderToBitmap` calls `DicomLoaderService.LoadPixelData` synchronously, which likely reads unloaded slices on the UI thread.

### Keyboard

- The `MainWindow.xaml.cs` branch that cycles sync mode on F5 is unreachable because the AI formatting handler runs first.

- A comment in `MainWindow.xaml.cs` says `Ctrl+M` opens 3D MPR, but it maximizes the panel.

### Accepted Because the App Is Private

- The Gemini key is stored in plain text in `gemini_key.txt` and sent in the request URL.

### Mouse Wheel vs Browse Series Drag Direction Synchronization (v1.3 Fix)
- **Problem**: Previously, rolling the mouse wheel down called NextImage() (slice index +1), whereas dragging the mouse down with Browse Series called StepImages(-steps) (slice index -1). This caused wheel scrolling and drag browsing to move in exact opposite directions (e.g. wheel down went cranial while drag down went caudal). Toggling Invert Scrolling simply inverted both, keeping them out of sync.
- **Fix**: In ViewportControl.xaml.cs (OnMouseWheel), MouseToolAction.BrowseSeries now executes m.StepImages(steps) (where steps = -1 on wheel down, matching drag down). Both wheel scroll and drag browse now advance and reverse slices in the exact same physical direction. Inverting scrolling flips both together consistently.

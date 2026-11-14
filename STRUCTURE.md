# Repository Architecture & Directory Map — STRUCTURE.md

This document outlines the directory structure of the **Parsec Medical DICOM Viewer** repository (`F:\Rad`). All agents and developers must adhere to this folder organization.

---

## 1. Visual Directory Tree

```
F:\Rad\
│
├── AGENT.md                       # Central Universal AI Agent Manual
├── CLAUDE.md                      # Claude Code reference manual and known issues
├── STRUCTURE.md                   # This directory architecture map
├── README.md                      # Project overview
├── .cursorrules                   # Cursor / Windsurf / Copilot AI IDE rules
├── Parsec_Setup_v1.3_*.exe        # Compiled installers (git-ignored)
├── Dist/                          # Download page + installer served on the LAN (untracked)
├── .claude/launch.json            # "parsec-download": serves Dist/ on port 8766
│
├── Installer\                     # Automated Installer & Packaging Suite
│   ├── build_installer.ps1        # Master PowerShell script producing the setup exe
│   ├── parsec.iss                 # Inno Setup script
│   ├── Setup\                     # Self-contained WPF setup application
│   │   ├── Parsec.Setup.csproj
│   │   ├── App.xaml / App.xaml.cs
│   │   └── MainWindow.xaml / MainWindow.xaml.cs
│   └── Uninstaller\               # Framework-dependent uninstall wizard
│       ├── Parsec.Uninstaller.csproj
│       ├── App.xaml / App.xaml.cs
│       └── MainWindow.xaml / MainWindow.xaml.cs
│
└── OpenRadViewer\                 # Main Parsec DICOM Viewer Application
    ├── OpenRadViewer.csproj       # Primary application project file (.NET 8.0-windows)
    ├── App.xaml / App.xaml.cs     # WPF entry point, single-instance, CLI argument routing
    ├── AssemblyInfo.cs            # WPF theme dictionary assembly attributes
    ├── app_icon.ico / app_icon.png
    ├── register_context_menu.reg  # Windows shell "Open with Parsec" entries
    │
    ├── Assets\                    # Flat folder; git-ignored except the two files marked below
    │   ├── .gitkeep               # Tracked
    │   ├── cfr_header_template.docx # Tracked: CFR hospital Word header
    │   ├── antet_ct_urgente.docx  # Emergency CT Word header
    │   ├── *_classification.*, *.png, *.jpg # Classification and reference images
    │   ├── modele_rezultate.txt   # Classic report examples
    │   ├── URGENTE CT.txt, REZULTATELE MELE URGENTE CT .txt # Emergency report examples
    │   └── vocabular_medical.txt  # Medical vocabulary for dictation prompts
    │
    ├── Core\                      # Pure domain and infrastructure engine
    │   ├── Config\                # User configurations and persistent settings
    │   │   └── MouseBindingConfig.cs   # Mouse actions, tools, gestures, JSON loading
    │   │
    │   ├── Models\                # Domain data models & entities
    │   │   ├── DicomSliceModel.cs      # Slice geometry, pixel buffers, window levels
    │   │   ├── PatientAndStudyModels.cs# Patient -> Study -> Series hierarchy
    │   │   ├── Measurement.cs          # 2D ROI tools, rulers, angles, Cobb, Spine
    │   │   ├── MprReferenceLine.cs     # MPR crosshairs and rotation handles
    │   │   ├── ReferenceGuideItem.cs   # Reference guide diagram/table models
    │   │   └── WindowPresetModel.cs    # Built-in and custom W/L presets
    │   │
    │   └── Services\              # Business logic & hardware services
    │       ├── DicomLoaderService.cs   # Streaming DICOM scanner, multi-frame, codecs
    │       ├── PixelRenderer.cs        # Unsafe pointer BGRA LUT rendering
    │       ├── MprService.cs           # 3D MPR volume reslicing (Ax/Cor/Sag/Obl)
    │       ├── SynchronizationService.cs # 3D spatial coordinate projection & cursor
    │       ├── PacsService.cs          # DICOM DIMSE client & C-STORE SCP listener
    │       ├── PacsModels.cs           # PACS configs, filter matching, SCP service
    │       ├── PixelDataPacsService.cs # PixelData web portal login, search, ZIP & slice download
    │       ├── ParsecPacsSyncService.cs# HTTP sync with ParsecPACS: notes (newest edit wins) and reviewed status
    │       ├── ArchiveExtractionService.cs # Opens .zip, .rar, .7z, .tar, .gz archives
    │       ├── D3D11VolumeRenderer.cs  # Direct3D 11 GPU raymarching engine
    │       ├── VolumeRenderingShader.hlsl # HLSL raymarching compute/pixel shader
    │       ├── VolumeRenderingService.cs# 3D presets (Bones, Angio, Skin, MIP, Airways)
    │       ├── AiService.cs            # Google Gemini & Claude consultation client
    │       ├── SpeechRecognitionService.cs # Dictation (MCI capture, Gemini, Whisper), F5 format, F6 refine
    │       ├── ClinicalProtocolsService.cs # CT/MRI report templates and auto-fill
    │       ├── DocxExportService.cs    # Word reports with CFR and emergency headers
    │       ├── DatabaseService.cs      # Local JSON study catalog: atomic updates, notes, sizes, delete
    │       ├── SplitSeriesService.cs   # Auto-splits DWI, multi-echo, dynamic phases
    │       ├── ExportService.cs        # Image/series encoder (PNG, JPG, TIF, BMP)
    │       ├── PatientInfoHelper.cs    # Age, sex, CNP parser
    │       ├── ReferenceGuideService.cs# User reference diagrams persistence
    │       ├── WindowPresetService.cs  # Built-in and custom W/L preset persistence
    │       ├── SingleInstanceManager.cs# Named Mutex & NamedPipe IPC
    │       ├── DarkWindowHelper.cs     # Win32 DWM immersive dark titlebars
    │       └── DicomSampleGenerator.cs # Synthetic MRI spine generator
    │
    ├── ViewModels\                # MVVM Presentation ViewModels
    │   ├── MainViewModel.cs       # Root state: layouts (1x1 to 4x4), active studies
    │   └── ViewportViewModel.cs   # Viewport tile state: series, slice, WL/WW, pan, zoom
    │
    └── Views\                     # UI Windows, Controls & Dialogs
        ├── MainWindow.xaml / .cs  # Shell window, custom title bar & toolbar, shortcuts
        ├── ViewportControl.xaml / .cs # Canvas, mouse gesture pipelines, HUD, drawings
        ├── SeriesPreviewBar.xaml / .cs # Left study thumbnail strip & drag source
        ├── ToolBindingIndicator.xaml / .cs # Colored mouse-binding dots on toolbar buttons
        ├── CustomTitleBar.cs      # Toolbar as title bar: chrome, maximized inset, caption drag
        ├── WindowControlsBar.xaml / .cs # Minimize, maximize/restore, and close buttons
        ├── PacsSearchWindow.xaml / .cs # PACS Query & Retrieve window + auto-refresh
        ├── PacsConfigurationWindow.xaml / .cs # DICOM and PixelData server setup & connection test
        ├── DatabaseWindow.xaml / .cs   # Local study catalog browser
        ├── VolumeRenderingWindow.xaml / .cs # 3D interactive volume rendering viewer
        ├── AiConsultationWindow.xaml / .cs # Multi-phase AI second opinion dialog
        ├── AiSettingsWindow.xaml / .cs # AI provider credentials & model config
        ├── VoiceSettingsDialog.xaml / .cs # Gemini key & model, custom rules, reference reports
        ├── UrgReportMetadataDialog.cs # Emergency report header fields
        ├── NotesEditDialog.cs     # Local study notes editor
        ├── AdrenalWashoutWindow.xaml / .cs # Adrenal CT washout percentage calculator
        ├── BosniakClassificationWindow.xaml / .cs # Bosniak classification viewer
        ├── FazekasClassificationWindow.xaml / .cs # Fazekas classification viewer
        ├── LiRadsClassificationWindow.xaml / .cs # LI-RADS classification viewer
        ├── PfirrmannClassificationWindow.xaml / .cs # Pfirrmann classification viewer
        ├── ScheltensClassificationWindow.xaml / .cs # Scheltens / MTA classification viewer
        ├── MouseSettingsDialog.xaml / .cs # Mouse button & modifier customization
        ├── CustomWindowDialog.xaml / .cs  # Precise WL/WW numerical input
        ├── ExportDialog.xaml / .cs        # Anonymized image/series exporter
        ├── PatientDetailsWindow.xaml / .cs # Patient metadata viewer
        ├── ReferenceGuideViewerWindow.xaml / .cs # Reference diagrams zoomable viewer
        ├── AddReferenceGuideDialog.xaml / .cs # Add custom classification diagram
        ├── Converters.cs          # WPF XAML ValueConverters
        ├── GridMatrixPickerControl.xaml / .cs # Viewport layout popup picker
        ├── AboutWindow.xaml / .cs # About dialog
        └── SplashWindow.xaml / .cs# Application startup splash screen
```

---

## 2. Directory Roles & Placement Rules

### `OpenRadViewer\Core\Models\`
- **Role:** Pure data structures, domain entities, and enums.
- **Allowed:** Plain C# objects (POCOs), entity classes, geometry definitions.
- **Forbidden:** WPF controls, UI event handlers, direct database writes, network calls.

### `OpenRadViewer\Core\Services\`
- **Role:** Algorithmic and platform services (DICOM decoding, Direct3D rendering, PACS network communication, mathematical calculations).
- **Allowed:** Reusable services, math operations, hardware interop, file loaders.
- **Forbidden:** WPF UI elements, direct ViewModel state mutation without notifications, modal dialog triggers.

### `OpenRadViewer\ViewModels\`
- **Role:** Application state and presentation logic following the MVVM pattern (`CommunityToolkit.Mvvm`).
- **Allowed:** Observable properties, relay commands, viewport management, workflow coordination.
- **Forbidden:** Direct manipulation of WPF UI controls (e.g. `System.Windows.Controls.Image`), native Win32 message pumps.

### `OpenRadViewer\Views\`
- **Role:** Visual presentation layer (WPF XAML and code-behind for viewports, controls, and dialogs).
- **Allowed:** Canvas rendering, drawing overlays, mouse gesture tracking, layout templates, ValueConverters.
- **Forbidden:** Heavy DICOM decoding loops, direct network socket communication, raw SQL or PACS parsing logic.

### `Installer\`
- **Role:** Packaging automation and installer/uninstaller builds.
- **Allowed:** Setup wizards, extraction utilities, shell association registry scripts.
- **Forbidden:** Application viewer logic or duplicated core services.

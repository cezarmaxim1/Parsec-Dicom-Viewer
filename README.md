# Parsec Medical DICOM Viewer

> [!CAUTION]
> ### ⚠️ MEDICAL DISCLAIMER
> **Parsec Medical DICOM Viewer is an experimental software created strictly for research, educational, workflow demonstration, and software testing purposes.**
> - **NOT A CERTIFIED MEDICAL DEVICE:** This software does **NOT** hold CE-MDD/CE-MDR (European Union), FDA 510(k) (United States), or any other regulatory medical device clearance or certification.
> - **NOT FOR PRIMARY DIAGNOSTIC OR CLINICAL USE:** This software **MUST NOT** be used for primary diagnostic interpretation, patient triage, therapeutic decisions, surgical planning, or any direct clinical care.
> - **NO LIABILITY:** The authors and contributors assume no legal, medical, or civil liability whatsoever for any direct, indirect, incidental, or consequential damages resulting from the use, operation, or misuse of this software.

---

## ⚡ Overview & Key Capabilities

**Parsec** is a high-performance, hardware-accelerated radiological desktop application built on **C# 12, .NET 8.0-windows, and WPF (DirectX 11 / HLSL)**, optimized for seamless 60+ FPS navigation, high-throughput DICOM streaming, and ergonomic reporting workflows.

### 1. High-Throughput Engine & GPU Acceleration
- **Zero-Latency Decoding:** Asynchronous streaming decoder powered by `fo-dicom` and native C/C++ codecs (`fo-dicom.Codecs`) supporting JPEG Lossless, JPEG 2000, JPEG-LS, and RLE.
- **Direct 16-Bit Cache:** Instantaneous 65,536-entry Window/Level LUT processing with unmanaged memory pointer copies directly into GPU/WPF writeable buffers.
- **3D Volume Rendering:** Direct3D 11 hardware-accelerated raymarching with transfer function presets (Bone, Soft Tissue, Angio, MIP).
- **Multi-Planar Reconstruction (MPR):** Real-time orthogonal (Axial, Sagittal, Coronal) and oblique reslicing with adjustable slab thickness and MIP/MinIP modes.

### 2. Multi-Patient & Longitudinal Comparison
- Open multiple patient studies side-by-side for longitudinal follow-up.
- Hierarchical structure: **Patient ➔ Studies (Date / Modality) ➔ Series ➔ 3D Spatially Sorted Slices**.
- Rigorous slice sorting derived from patient geometry vectors (`ImagePositionPatient` & `ImageOrientationPatient`).

### 3. Dynamic Multi-Viewport Matrix
- Grid matrix layouts from 1×1, 1×2, 2×2, 3×3 up to **6×5 (30 concurrent viewports)**.
- Instant single-panel maximization and restore with `Ctrl+M`.
- Active viewport indicator and rapid keyboard switching with `Tab` / `Shift+Tab`.

### 4. Cross-Series Spatial Synchronization & 3D Localizer
- **True 3D Spatial Registration:** Automatically locates the physically closest slice in adjacent series or prior studies using real-world patient coordinates.
- Synchronized Zoom, Pan, and Window/Level adjustments.
- Real-time 3D reference lines and localizer scout crosshairs.

### 5. Advanced Clinical Annotations & Measurements
- Calibrated 2D tools: Length, Ellipse / Circular ROI (with Area, Mean, Min, Max, StDev), Angle, Cobb Angle, Arrow pointers, and Vertebral spine labeling.
- Preset CT/MRI windowing (`1` - Dynamic, `2` - Abdomen, `3` - Angio, `4` - Bone, `5` - Brain, `6` - Thorax, `7` - Lung).
- Invert monochrome (`F11`), continuous Cine playback (`Space`), and geometric flips/rotations.

### 6. Built-in PACS Networking
- Full DIMSE networking support: **C-ECHO**, **C-FIND** (Query), **C-MOVE** (Retrieve), and local **C-STORE SCP** listener service.
- Local PACS synchronization and direct DICOM export.

---

## ⌨️ Default Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| **Ctrl + Shift + O** | Scan DICOM folder recursively |
| **Ctrl + O** | Open DICOM files |
| **Ctrl + E** | Export image(s) to PNG, JPEG, BMP, or TIFF |
| **F5** | Toggle cross-series synchronization mode (Off / Manual / Auto) |
| **F4** | Cycle grid viewport layouts (1×1, 1×2, 2×2, 3×3) |
| **Tab / Shift + Tab** | Select next / previous viewport |
| **Ctrl + M** | Maximize / restore active viewport |
| **Ctrl + F4** | Close active viewport |
| **Shift + F4** | Close all viewports |
| **B / W / M / Z** | Select tool: Browse / Window Level / Pan / Zoom |
| **L / E / A / C / Y** | Measurement tools: Length / Ellipse / Angle / Cobb / Arrow |
| **0 .. 9** | CT / MRI Window Presets |
| **F11** | Negative (invert grayscale) |
| **Space** | Play / Pause continuous Cine loop |
| **Up / Down Arrows** | Previous / next slice |
| **Page Up / Page Down** | Jump 10 slices backward / forward |
| **Home / End** | First / last slice |
| **Ctrl + [ / Ctrl + ]** | Rotate 90° CCW / CW |
| **Ctrl + Shift + [ / ]** | Flip Horizontal / Vertical |
| **F** | Toggle Full Screen |
| **Ctrl + Alt + F2** | Toggle Series Preview Bar |

---

## 🛠️ Build & Installation

### Requirements
- **OS:** Windows 10 / 11 (64-bit)
- **SDK:** [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Development Build
```powershell
# Restore dependencies and build
dotnet build OpenRadViewer\OpenRadViewer.csproj -c Debug

# Run the viewer
dotnet run --project OpenRadViewer\OpenRadViewer.csproj
```

### Self-Contained Release Packaging
```powershell
powershell -ExecutionPolicy Bypass -File Installer\build_installer.ps1
```

---

## ⚖️ License

Distributed under the **MIT License**. Please refer to the Medical Disclaimer above prior to any use.

# Parsec Medical DICOM Viewer

> [!CAUTION]
> ### ⚠️ MEDICAL DISCLAIMER / AVERTISMENT MEDICAL
> **EN:** Parsec Medical DICOM Viewer is an experimental software created strictly for **research, educational, workflow demonstration, and software testing purposes**.
> - **NOT A CERTIFIED MEDICAL DEVICE:** This software does **NOT** hold CE-MDD/CE-MDR (European Union), FDA 510(k) (United States), ANMDMR, or any other regulatory medical device clearance or certification.
> - **NOT FOR PRIMARY DIAGNOSTIC OR CLINICAL USE:** This software **MUST NOT** be used for primary diagnostic interpretation, patient triage, therapeutic decisions, surgical planning, or any direct clinical care.
> - **NO LIABILITY:** The authors and contributors assume no legal, medical, or civil liability whatsoever for any direct, indirect, incidental, or consequential damages resulting from the use, operation, or misuse of this software.
>
> **RO:** Parsec Medical DICOM Viewer este un software experimental conceput exclusiv pentru **cercetare, instruire educațională și demonstrarea optimizării fluxului de lucru tehnic**.
> - **NU ESTE UN DISPOZITIV MEDICAL CERTIFICAT:** Această aplicație **NU** deține marcaj sau certificare CE-MDR/CE-MDD, autorizare FDA 510(k), aviz ANMDMR sau orice altă acreditare de dispozitiv medical.
> - **INTERZISĂ UTILIZAREA PENTRU DIAGNOSTIC CLINIC:** Este **STRICT INTERZISĂ** folosirea programului pentru diagnostic primar, interpretare medicală oficială, triaj sau decizii terapeutice asupra pacienților.
> - **DECLINARE DE RESPONSABILITATE:** Autorul și colaboratorii nu își asumă nicio responsabilitate juridică, medicală sau civilă pentru decizii ori daune rezultate din utilizarea acestui program.

---

## ⚡ Overview & Features

**Parsec** este un DICOM Viewer medical de înaltă performanță, scris în **C# (.NET 8)** pentru Windows (WPF + DirectX 11), optimizat pentru fluiditate maximă (60+ FPS la scroll și ajustare de contrast).

1. **Performanță Nativă & Randare GPU:**
   - Decodare asincronă cu `fo-dicom` și `fo-dicom.Codecs` (JPEG, JPEG 2000, RLE).
   - Memorie cache directă pe 16 biți (fără re-citire de pe disc la scroll).
   - Calcul LUT (Lookup Table) instantaneu pe 65.536 de intrări pentru Window/Level.
   - Randare cu pointeri nativi direct în bufferul GPU/WPF (`WriteableBitmap`) și 3D Volume Rendering cu Direct3D 11 / HLSL.

2. **Multi-Pacient și Studii Multiple (Comparație Longitudinală):**
   - Suportă deschiderea mai multor pacienți simultan sau a aceluiași pacient la date de examinare diferite.
   - Ierarhie: **Pacient -> Studii (Dată / Clinică) -> Serii -> Felii sortate spațial în 3D**.
   - Sortare riguroasă a feliilor calculată din vectorul normal al pacientului (`ImagePositionPatient` & `ImageOrientationPatient`).

3. **Layout Dinamic Multi-Panel (Split până la 6×5 = 30 Viewport-uri):**
   - Grile rapide: 1×1, 1×2, 2×1, 2×2, 2×3, 3×3, 4×3, 5×4, până la **6×5**.
   - Maximizare / restaurare panou activ cu `Ctrl+M`.
   - Evidențiere galbenă (chihlimbar RadiAnt) a panoului activ.
   - Navigare între panouri cu `Tab` / `Shift+Tab`.

4. **Sincronizare Între Serii (`F5`):**
   - Sincronizare spațială 3D a poziției feliei (găsește automat felia cea mai apropiată fizic în spațiul pacientului dintr-o altă serie sau dată).
   - Sincronizare Zoom & Pan.
   - Sincronizare setări fereastră (Window / Level).

5. **Configurare Completă pentru Mouse (până la 7 Butoane + Rotiță):**
   - Fereastră dedicată de setări (accesibilă din meniu sau toolbar).
   - Mapare acțiuni pentru `LeftDrag`, `RightDrag`, `MiddleDrag`, `XButton1Drag` (Mouse 4), `XButton2Drag` (Mouse 5), `WheelScroll`.

6. **Export & Unelte:**
   - Export felie curentă sau serie completă în format PNG, JPEG, BMP sau TIFF (`Ctrl+E`).
   - Preset-uri de fereastră CT/RMN (`0` - Implicit, `1` - Dinamic, `2` - Abdomen, `3` - Angio, `4` - Os, `5` - Creier, `6` - Torace, `7` - Plămân).
   - Inversare imagine (Negativ - `F11`).
   - Rotații la 90° (`Ctrl+[` / `Ctrl+]`) și oglindire orizontală/verticală.
   - Mod Cine Playback continuu (`Space`).
   - Reconstrucție MPR 3D (Axial, Sagital, Coronal) și randare volumetrică Direct3D 11.

---

## ⌨️ Comenzi Rapide de la Tastatură (Shortcuts)

| Shortcut | Acțiune |
| :--- | :--- |
| **Ctrl + Shift + O** | Scanare folder DICOM recursiv |
| **Ctrl + O** | Deschidere fișiere DICOM |
| **Ctrl + E** | Export imagini (PNG / JPEG / BMP / TIFF) |
| **F5** | Comutare mod sincronizare serii |
| **F4** | Ciclare layout ferestre (1×1, 1×2, 2×2, 3×3) |
| **Tab / Shift + Tab** | Activare panou următor / anterior |
| **Ctrl + M** | Maximizare / restaurare panou activ |
| **Ctrl + F4** | Închidere panou activ |
| **Shift + F4** | Închidere toate panourile |
| **B / W / M / Z** | Selectare unealtă: Browse / Window Level / Pan / Zoom |
| **L / E / A / C / Y** | Unelte măsurare: Lungime / Elipsă / Unghi / Cobb / Săgeată |
| **0 .. 9** | Preseturile de fereastră (Implicit, Dinamic, Abdomen, Os, Plămân, etc.) |
| **F11** | Negativ (inversare nuanțe gri) |
| **Space** | Pornire / oprire buclă Cine |
| **Săgeți Sus / Jos** | Felia anterioară / următoare |
| **Page Up / Page Down** | Salt 10 felii înapoi / înainte |
| **Home / End** | Prima / ultima felie |
| **Ctrl + [ / Ctrl + ]** | Rotire 90° CCW / CW |
| **Ctrl + Shift + [ / ]** | Oglindire orizontală / verticală |
| **F** | Comutare ecran complet (Full Screen) |
| **Ctrl + Alt + F2** | Afișare / ascundere bară de serii |

---

## 🛠️ Build & Dezvoltare

Proiectul necesită **.NET 8.0 SDK** pe Windows:
```powershell
# Compilare soluție
dotnet build OpenRadViewer\OpenRadViewer.csproj -c Debug

# Rulare aplicație
dotnet run --project OpenRadViewer\OpenRadViewer.csproj
```

---

## ⚖️ Licență

Acest software este distribuit sub licență **MIT**. Consultați secțiunea de avertisment medical din partea superioară a documentului înainte de orice utilizare.

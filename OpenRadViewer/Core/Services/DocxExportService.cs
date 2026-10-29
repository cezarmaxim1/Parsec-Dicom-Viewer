// @purpose: Standalone DOCX exporter generating standard Microsoft Word documents (Times New Roman 12pt, clinical layout with first-line indentation) from clinical reports.
// @depends: System.IO.Compression, System.Text.RegularExpressions
// @exports: DocxExportService

using System;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenRadViewer.Core.Services;

public static class DocxExportService
{
    /// <summary>
    /// Exports plain text clinical report into a styled Microsoft Word (.docx) document
    /// preserving the exact official hospital header (logos, borders, table, and typography from CT.docx / IRM.docx)
    /// and formatting the body in Times New Roman 12pt with proper clinical indentation and alignment.
    /// </summary>
    public static void SaveTextToDocx(string text, string targetFilePath, string title = "Raport Medical")
    {
        if (File.Exists(targetFilePath))
        {
            File.Delete(targetFilePath);
        }

        // 1. Try to clone the official CFR hospital template (CT.docx / IRM.docx / cfr_header_template.docx)
        // so that word/header1..3.xml, word/footer1..3.xml, word/media/image1..3, and relationships are copied 100% identically.
        bool hasTemplate = TryCopyHeaderTemplateDocx(targetFilePath);

        if (hasTemplate)
        {
            using var zip = ZipFile.Open(targetFilePath, ZipArchiveMode.Update);
            var existingDocEntry = zip.GetEntry("word/document.xml");

            string docPrefix = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:document xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"" xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
  <w:body>";
            string sectPrXml = @"<w:sectPr>
  <w:headerReference w:type=""even"" r:id=""rId6""/>
  <w:headerReference w:type=""default"" r:id=""rId7""/>
  <w:footerReference w:type=""even"" r:id=""rId8""/>
  <w:footerReference w:type=""default"" r:id=""rId9""/>
  <w:headerReference w:type=""first"" r:id=""rId10""/>
  <w:footerReference w:type=""first"" r:id=""rId11""/>
  <w:pgSz w:w=""12240"" w:h=""15840""/>
  <w:pgMar w:top=""1440"" w:right=""1440"" w:bottom=""1440"" w:left=""1440"" w:header=""708"" w:footer=""708"" w:gutter=""0""/>
  <w:cols w:space=""708""/>
  <w:docGrid w:linePitch=""360""/>
</w:sectPr>";

            if (existingDocEntry != null)
            {
                string origXml;
                using (var sr = new StreamReader(existingDocEntry.Open(), Encoding.UTF8))
                {
                    origXml = sr.ReadToEnd();
                }

                int bodyIdx = origXml.IndexOf("<w:body>", StringComparison.Ordinal);
                if (bodyIdx >= 0)
                {
                    docPrefix = origXml.Substring(0, bodyIdx + "<w:body>".Length);
                }

                int sectIdx = origXml.LastIndexOf("<w:sectPr", StringComparison.Ordinal);
                int sectEndIdx = origXml.LastIndexOf("</w:sectPr>", StringComparison.Ordinal);
                if (sectIdx >= 0 && sectEndIdx > sectIdx)
                {
                    sectPrXml = origXml.Substring(sectIdx, (sectEndIdx + "</w:sectPr>".Length) - sectIdx);
                }

                existingDocEntry.Delete();
            }

            var newDocEntry = zip.CreateEntry("word/document.xml", CompressionLevel.Fastest);
            using var writer = new StreamWriter(newDocEntry.Open(), Encoding.UTF8);
            var sb = new StringBuilder();
            sb.Append(docPrefix);
            AppendBodyParagraphs(sb, text, includeFallbackTopLine: false);
            sb.Append(sectPrXml);
            sb.Append("</w:body></w:document>");
            writer.Write(sb.ToString());
            return;
        }

        // Fallback if no template file is available: create standalone .docx
        using (var zip = ZipFile.Open(targetFilePath, ZipArchiveMode.Create))
        {
            // 1. [Content_Types].xml
            var contentTypesEntry = zip.CreateEntry("[Content_Types].xml", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(contentTypesEntry.Open(), Encoding.UTF8))
            {
                writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""xml"" ContentType=""application/xml""/>
  <Override PartName=""/word/document.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml""/>
  <Override PartName=""/word/styles.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml""/>
  <Override PartName=""/word/settings.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml""/>
</Types>");
            }

            // 2. _rels/.rels
            var relsEntry = zip.CreateEntry("_rels/.rels", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(relsEntry.Open(), Encoding.UTF8))
            {
                writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""word/document.xml""/>
</Relationships>");
            }

            // 3. word/_rels/document.xml.rels
            var docRelsEntry = zip.CreateEntry("word/_rels/document.xml.rels", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(docRelsEntry.Open(), Encoding.UTF8))
            {
                writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/>
  <Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings"" Target=""settings.xml""/>
</Relationships>");
            }

            // 4. word/styles.xml
            var stylesEntry = zip.CreateEntry("word/styles.xml", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(stylesEntry.Open(), Encoding.UTF8))
            {
                writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:styles xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
  <w:docDefaults>
    <w:rPrDefault>
      <w:rPr>
        <w:rFonts w:ascii=""Times New Roman"" w:hAnsi=""Times New Roman"" w:cs=""Times New Roman""/>
        <w:sz w:val=""24""/>
        <w:szCs w:val=""24""/>
        <w:lang w:val=""ro-RO"" w:eastAsia=""ro-RO"" w:bidi=""ar-SA""/>
      </w:rPr>
    </w:rPrDefault>
    <w:pPrDefault>
      <w:pPr>
        <w:spacing w:before=""0"" w:after=""0"" w:line=""240"" w:lineRule=""auto""/>
      </w:pPr>
    </w:pPrDefault>
  </w:docDefaults>
  <w:style w:type=""paragraph"" w:default=""1"" w:styleId=""Normal"">
    <w:name w:val=""Normal""/>
    <w:qFormat/>
    <w:rPr>
      <w:rFonts w:ascii=""Times New Roman"" w:hAnsi=""Times New Roman"" w:cs=""Times New Roman""/>
      <w:sz w:val=""24""/>
      <w:szCs w:val=""24""/>
      <w:lang w:val=""ro-RO"" w:eastAsia=""ro-RO"" w:bidi=""ar-SA""/>
    </w:rPr>
  </w:style>
</w:styles>");
            }

            // 5. word/settings.xml
            var settingsEntry = zip.CreateEntry("word/settings.xml", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(settingsEntry.Open(), Encoding.UTF8))
            {
                writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:settings xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
  <w:themeFontLang w:val=""ro-RO""/>
  <w:compat>
    <w:compatSetting w:name=""compatibilityMode"" w:uri=""http://schemas.microsoft.com/office/word"" w:val=""15""/>
  </w:compat>
</w:settings>");
            }

            // 6. word/document.xml
            var docEntry = zip.CreateEntry("word/document.xml", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(docEntry.Open(), Encoding.UTF8))
            {
                var sb = new StringBuilder();
                sb.Append(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
  <w:body>");
                AppendBodyParagraphs(sb, text, includeFallbackTopLine: true);
                sb.Append(@"<w:sectPr>
  <w:pgSz w:w=""11906"" w:h=""16838""/>
  <w:pgMar w:top=""1440"" w:right=""1440"" w:bottom=""1440"" w:left=""1440"" w:header=""720"" w:footer=""720"" w:gutter=""0""/>
</w:sectPr>
</w:body>
</w:document>");
                writer.Write(sb.ToString());
            }
        }
    }

    private static bool TryCopyHeaderTemplateDocx(string targetFilePath)
    {
        try
        {
            string appDataRefDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Parsec",
                "ReferenceReports");

            string baseAssetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");

            string[] priorityCandidates = new[]
            {
                Path.Combine(appDataRefDir, "CT.docx"),
                Path.Combine(appDataRefDir, "IRM.docx"),
                Path.Combine(baseAssetsDir, "cfr_header_template.docx")
            };

            foreach (var candidate in priorityCandidates)
            {
                if (File.Exists(candidate))
                {
                    File.Copy(candidate, targetFilePath, overwrite: true);
                    return true;
                }
            }

            if (Directory.Exists(appDataRefDir))
            {
                foreach (var docxFile in Directory.GetFiles(appDataRefDir, "*.docx"))
                {
                    try
                    {
                        using var checkZip = ZipFile.OpenRead(docxFile);
                        if (checkZip.GetEntry("word/header2.xml") != null || checkZip.GetEntry("word/header1.xml") != null)
                        {
                            checkZip.Dispose();
                            File.Copy(docxFile, targetFilePath, overwrite: true);
                            return true;
                        }
                    }
                    catch { }
                }
            }

            // Fallback to embedded WPF resource pack://application:,,,/Assets/cfr_header_template.docx
            var resInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/cfr_header_template.docx"));
            if (resInfo?.Stream != null)
            {
                using var srcStream = resInfo.Stream;
                using var dstStream = File.Create(targetFilePath);
                srcStream.CopyTo(dstStream);
                return true;
            }
        }
        catch { }

        return false;
    }

    private static void AppendBodyParagraphs(StringBuilder sb, string? text, bool includeFallbackTopLine)
    {
        const string rPrBase = @"<w:rPr><w:rFonts w:ascii=""Times New Roman"" w:hAnsi=""Times New Roman"" w:cs=""Times New Roman""/><w:sz w:val=""24""/><w:szCs w:val=""24""/><w:lang w:val=""ro-RO"" w:eastAsia=""ro-RO"" w:bidi=""ar-SA""/></w:rPr>";
        const string rPrBold = @"<w:rPr><w:rFonts w:ascii=""Times New Roman"" w:hAnsi=""Times New Roman"" w:cs=""Times New Roman""/><w:b/><w:sz w:val=""24""/><w:szCs w:val=""24""/><w:lang w:val=""ro-RO"" w:eastAsia=""ro-RO"" w:bidi=""ar-SA""/></w:rPr>";

        if (includeFallbackTopLine)
        {
            sb.Append(@"<w:p>
  <w:pPr>
    <w:pBdr>
      <w:bottom w:val=""single"" w:sz=""6"" w:space=""1"" w:color=""A0A0A0""/>
    </w:pBdr>
    <w:spacing w:before=""0"" w:after=""120"" w:line=""240"" w:lineRule=""auto""/>
  </w:pPr>
</w:p>");
        }

        string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        bool inFooterZone = false;

        foreach (var rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.Append($@"<w:p><w:pPr><w:spacing w:before=""0"" w:after=""0"" w:line=""240"" w:lineRule=""auto""/></w:pPr><w:r>{rPrBase}</w:r></w:p>");
                continue;
            }

            string trimmed = line.TrimStart();

            // Detect centered main title: "REZULTAT EXAMEN IRM" or "REZULTAT EXAMEN CT"
            bool isCenteredTitle = trimmed.StartsWith("REZULTAT EXAMEN", StringComparison.OrdinalIgnoreCase);

            // Detect patient header lines or footer date/doctor lines
            bool isHeaderLine = trimmed.StartsWith("Nume:", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("Prenume:", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("Zona examinată:", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("Zona examinata:", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("DLP:", StringComparison.OrdinalIgnoreCase);

            bool isFooterLine = trimmed.StartsWith("DATA", StringComparison.OrdinalIgnoreCase) ||
                                (inFooterZone && (trimmed.Contains("Dr.", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(trimmed, @"^\d{2}\.\d{2}\.\d{4}")));

            if (trimmed.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
            {
                inFooterZone = true;
            }

            // Detect bold section headers (e.g., "COLOANA CERVICALA:", "COLOANA LOMBARA:", "CONCLUZII:", etc.)
            bool isSectionHeader = !isCenteredTitle && !isHeaderLine && !isFooterLine &&
                                   (trimmed.StartsWith("CONCLUZII", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("COLOANA", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("CRANIO", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("TORACE", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("ABDOMIN", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("PELVIS", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("GENUNCHI", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("HIPOFIZA", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("MRCP", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("ENTERO", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("BAZIN", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("COXO-FEMURAL", StringComparison.OrdinalIgnoreCase) ||
                                    trimmed.StartsWith("TEP", StringComparison.OrdinalIgnoreCase) ||
                                    (trimmed.EndsWith(":") && trimmed == trimmed.ToUpperInvariant() && trimmed.Length <= 45));

            if (isCenteredTitle)
            {
                string escaped = SecurityElement.Escape(trimmed) ?? "";
                sb.Append("<w:p>");
                sb.Append(@"<w:pPr><w:jc w:val=""center""/><w:spacing w:before=""0"" w:after=""0"" w:line=""240"" w:lineRule=""auto""/></w:pPr>");
                sb.Append("<w:r>");
                sb.Append(rPrBold);
                sb.Append("<w:t xml:space=\"preserve\">").Append(escaped).Append("</w:t>");
                sb.Append("</w:r>");
                sb.Append("</w:p>");
            }
            else if (isHeaderLine)
            {
                // Split into left and right columns if second field exists on the same line
                var (leftCol, rightCol, tabPos) = SplitTwoColumnLine(trimmed);
                sb.Append("<w:p>");
                sb.Append($"<w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"{tabPos}\"/></w:tabs><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr>");

                sb.Append("<w:r>");
                sb.Append(rPrBase);
                sb.Append("<w:t xml:space=\"preserve\">").Append(SecurityElement.Escape(leftCol) ?? "").Append("</w:t>");
                if (!string.IsNullOrEmpty(rightCol))
                {
                    sb.Append("<w:tab/>");
                    sb.Append("<w:t xml:space=\"preserve\">").Append(SecurityElement.Escape(rightCol) ?? "").Append("</w:t>");
                }
                sb.Append("</w:r>");
                sb.Append("</w:p>");
            }
            else if (isFooterLine)
            {
                // Footer lines: indent first column with 1 TAB (720 dxa = 1.27 cm) to align parallel with body paragraphs,
                // and place second column (MEDIC EXAMINATOR / Dr.) at 5000 dxa (~4-5 tabs distance).
                var (leftCol, rightCol, _) = SplitTwoColumnLine(trimmed.TrimStart('\t', ' '));
                sb.Append("<w:p>");
                sb.Append("<w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/><w:tab w:val=\"left\" w:pos=\"5000\"/></w:tabs><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr>");

                sb.Append("<w:r>");
                sb.Append(rPrBase);
                sb.Append("<w:tab/>"); // 1 TAB before DATA / Date to align with paragraph first-line indent
                sb.Append("<w:t xml:space=\"preserve\">").Append(SecurityElement.Escape(leftCol) ?? "").Append("</w:t>");
                if (!string.IsNullOrEmpty(rightCol))
                {
                    sb.Append("<w:tab/>");
                    sb.Append("<w:t xml:space=\"preserve\">").Append(SecurityElement.Escape(rightCol) ?? "").Append("</w:t>");
                }
                sb.Append("</w:r>");
                sb.Append("</w:p>");
            }
            else
            {
                // Normal clinical paragraph or section header -> first-line indent (720 dxa = 1.27 cm)
                string escaped = SecurityElement.Escape(trimmed) ?? "";
                sb.Append("<w:p>");
                sb.Append(@"<w:pPr><w:ind w:firstLine=""720""/><w:spacing w:before=""0"" w:after=""0"" w:line=""240"" w:lineRule=""auto""/></w:pPr>");
                sb.Append("<w:r>");
                sb.Append(isSectionHeader ? rPrBold : rPrBase);
                sb.Append("<w:t xml:space=\"preserve\">").Append(escaped).Append("</w:t>");
                sb.Append("</w:r>");
                sb.Append("</w:p>");
            }
        }
    }

    private static (string Left, string Right, int TabPos) SplitTwoColumnLine(string line)
    {
        // Match right-hand column markers: CNP, Vârsta, Contrast, MEDIC EXAMINATOR, Dr.
        string[] rightMarkers = new[]
        {
            "CNP/ID:", "CNP:", "Vârsta:", "Varsta:", "Contrast:", "MEDIC EXAMINATOR", "Dr."
        };

        foreach (var marker in rightMarkers)
        {
            int idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                string left = line.Substring(0, idx).Trim();
                string right = line.Substring(idx).Trim();
                // Align CNP, Vârsta, Contrast, and MEDIC EXAMINATOR on the EXACT SAME vertical column (5000 dxa)
                int tabPos = 5000;
                return (left, right, tabPos);
            }
        }

        // Fallback if separated by tabs or 3+ spaces
        var match = Regex.Match(line, @"^(.+?)(\t+|\s{3,})(.+)$");
        if (match.Success)
        {
            return (match.Groups[1].Value.Trim(), match.Groups[3].Value.Trim(), 5000);
        }

        return (line, string.Empty, 5000);
    }

    /// <summary>
    /// Exports emergency CT report into a Microsoft Word document (.docx)
    /// cloning the official hospital emergency template (antet_ct_urgente.docx)
    /// with the exact hospital header (Sf. Spiridon), footer, metadata lines,
    /// report body text (spaced by 1 empty line before and after), and doctor signatures table.
    /// </summary>
    public static void SaveTextToUrgDocx(
        string bodyText,
        string targetFilePath,
        string patientName,
        string cnp,
        string examDate,
        string examName,
        string contrast,
        string dlp,
        string nr = "",
        string clinica = "",
        string doctor = "",
        string fo = "",
        string diagnostic = "")
    {
        if (File.Exists(targetFilePath))
        {
            File.Delete(targetFilePath);
        }

        bool hasTemplate = TryCopyUrgHeaderTemplateDocx(targetFilePath);
        if (!hasTemplate)
        {
            // If template not found, fallback to standard Docx export
            SaveTextToDocx(bodyText, targetFilePath, $"Raport URG {patientName}");
            return;
        }

        using var zip = ZipFile.Open(targetFilePath, ZipArchiveMode.Update);
        var existingDocEntry = zip.GetEntry("word/document.xml");
        if (existingDocEntry == null) return;

        string origXml;
        using (var sr = new StreamReader(existingDocEntry.Open(), Encoding.UTF8))
        {
            origXml = sr.ReadToEnd();
        }

        int bodyIdx = origXml.IndexOf("<w:body>", StringComparison.Ordinal);
        string docPrefix = bodyIdx >= 0 
            ? origXml.Substring(0, bodyIdx + "<w:body>".Length)
            : @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><w:document xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"" xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main""><w:body>";

        // Extract doctor signatures table <w:tbl>...</w:tbl>
        string tblXml = "";
        int tblStart = origXml.IndexOf("<w:tbl", StringComparison.Ordinal);
        int tblEnd = origXml.IndexOf("</w:tbl>", StringComparison.Ordinal);
        if (tblStart >= 0 && tblEnd > tblStart)
        {
            tblXml = origXml.Substring(tblStart, (tblEnd + "</w:tbl>".Length) - tblStart);
        }

        // Extract section properties <w:sectPr>...</w:sectPr>
        string sectPrXml = "";
        int sectIdx = origXml.LastIndexOf("<w:sectPr", StringComparison.Ordinal);
        int sectEndIdx = origXml.LastIndexOf("</w:sectPr>", StringComparison.Ordinal);
        if (sectIdx >= 0 && sectEndIdx > sectIdx)
        {
            sectPrXml = origXml.Substring(sectIdx, (sectEndIdx + "</w:sectPr>".Length) - sectIdx);
        }

        existingDocEntry.Delete();

        var newDocEntry = zip.CreateEntry("word/document.xml", CompressionLevel.Fastest);
        using (var writer = new StreamWriter(newDocEntry.Open(), Encoding.UTF8))
        {
            var sb = new StringBuilder();
            sb.Append(docPrefix);

            // Title: BULETIN DE EXAMINARE COMPUTER-TOMOGRAFICĂ
            sb.Append(@"<w:p><w:pPr><w:jc w:val=""center""/><w:rPr><w:b/><w:sz w:val=""26""/><w:szCs w:val=""26""/><w:lang w:val=""ro-RO""/></w:rPr></w:pPr><w:r><w:rPr><w:b/><w:sz w:val=""26""/><w:szCs w:val=""26""/><w:lang w:val=""ro-RO""/></w:rPr><w:t>BULETIN DE EXAMINARE COMPUTER-TOMOGRAFICĂ</w:t></w:r></w:p>");

            // Registration & Date: URG Nr. {nr} Din {examDate}
            string nrPart = string.IsNullOrWhiteSpace(nr) ? " " : $" {SecurityElement.Escape(nr)} ";
            sb.Append($@"<w:p><w:pPr><w:rPr><w:b w:val=""0""/><w:lang w:val=""ro-RO""/></w:rPr></w:pPr><w:r><w:rPr><w:b w:val=""0""/><w:lang w:val=""ro-RO""/></w:rPr><w:t>URG Nr.{nrPart}Din {SecurityElement.Escape(examDate)}</w:t></w:r></w:p>");

            // Empty line
            sb.Append(@"<w:p><w:pPr><w:jc w:val=""both""/></w:pPr></w:p>");

            // PACIENT & CNP
            sb.Append($@"<w:p><w:pPr><w:jc w:val=""both""/><w:rPr><w:bCs/></w:rPr></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>PACIENT: </w:t></w:r><w:r><w:t>{SecurityElement.Escape(patientName.ToUpperInvariant())}</w:t></w:r><w:r><w:tab/><w:tab/><w:tab/><w:tab/><w:tab/></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>CNP: </w:t></w:r><w:r><w:t>{SecurityElement.Escape(cnp)}</w:t></w:r></w:p>");

            // CLINICA/DR & FO
            string clinVal = string.IsNullOrWhiteSpace(clinica) ? "UPU" : clinica;
            string clinDrText = !string.IsNullOrWhiteSpace(doctor) ? $"{clinVal} /DR. {doctor.ToUpperInvariant()}" : $"{clinVal} /DR.";
            sb.Append($@"<w:p><w:pPr><w:jc w:val=""both""/><w:rPr><w:bCs/></w:rPr></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>CLINICA/DR: </w:t></w:r><w:r><w:t>{SecurityElement.Escape(clinDrText)}</w:t></w:r><w:r><w:tab/><w:tab/><w:tab/><w:tab/><w:tab/></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>FO: </w:t></w:r><w:r><w:t>{SecurityElement.Escape(fo)}</w:t></w:r></w:p>");

            // DIAGNOSTIC DE TRIMITERE
            sb.Append($@"<w:p><w:pPr><w:jc w:val=""both""/><w:rPr><w:bCs/></w:rPr></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>DIAGNOSTIC DE TRIMITERE: </w:t></w:r><w:r><w:t>{SecurityElement.Escape(diagnostic)}</w:t></w:r></w:p>");

            // EXAMEN REALIZAT
            sb.Append($@"<w:p><w:pPr><w:jc w:val=""both""/><w:rPr><w:bCs/></w:rPr></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>EXAMEN REALIZAT: </w:t></w:r><w:r><w:t>{SecurityElement.Escape(examName)}</w:t></w:r></w:p>");

            // SUBSTANTA DE CONTRAST & DLP
            string dlpValue = string.IsNullOrWhiteSpace(dlp) ? "" : $"{SecurityElement.Escape(dlp)} ";
            sb.Append($@"<w:p><w:pPr><w:jc w:val=""both""/><w:rPr><w:bCs/></w:rPr></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>SUBSTANTA DE CONTRAST: </w:t></w:r><w:r><w:t>{SecurityElement.Escape(contrast)}</w:t></w:r><w:r><w:tab/><w:tab/><w:tab/><w:tab/></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>DLP: </w:t></w:r><w:r><w:t>{dlpValue}mGy*cm</w:t></w:r></w:p>");

            // UN RAND SPATIU INAINTE DE TEXT
            sb.Append(@"<w:p><w:pPr><w:jc w:val=""both""/></w:pPr></w:p>");

            // TEXT / INTERPRETARE RAPORT
            AppendUrgBodyParagraphs(sb, bodyText);

            // UN RAND SPATIU DUPA TEXT
            sb.Append(@"<w:p><w:pPr><w:jc w:val=""both""/></w:pPr></w:p>");

            // TABELUL DE SEMNATURI AL DOCTORILOR
            if (!string.IsNullOrWhiteSpace(tblXml))
            {
                sb.Append(tblXml);
            }

            // POST-TABLE BLANK LINE
            sb.Append(@"<w:p><w:pPr><w:jc w:val=""both""/></w:pPr></w:p>");

            // SECTPR (header & footer references)
            if (!string.IsNullOrWhiteSpace(sectPrXml))
            {
                sb.Append(sectPrXml);
            }

            sb.Append(@"</w:body></w:document>");
            writer.Write(sb.ToString());
        }
    }

    private static bool TryCopyUrgHeaderTemplateDocx(string targetFilePath)
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            string[] priorityCandidates = new[]
            {
                Path.Combine(baseDir, "Assets", "antet_ct_urgente.docx"),
                Path.Combine(appData, "Parsec", "Assets", "antet_ct_urgente.docx"),
                Path.Combine(appData, "Parsec", "antet_ct_urgente.docx"),
                @"F:\Desktop\antet ct urgente.docx"
            };

            foreach (var candidate in priorityCandidates)
            {
                if (File.Exists(candidate))
                {
                    File.Copy(candidate, targetFilePath, overwrite: true);
                    return true;
                }
            }

            var resInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/antet_ct_urgente.docx"));
            if (resInfo?.Stream != null)
            {
                using var srcStream = resInfo.Stream;
                using var dstStream = File.Create(targetFilePath);
                srcStream.CopyTo(dstStream);
                return true;
            }
        }
        catch { }

        return false;
    }

    private static void AppendUrgBodyParagraphs(StringBuilder sb, string? text)
    {
        const string rPrBase = @"<w:rPr><w:rFonts w:ascii=""Times New Roman"" w:hAnsi=""Times New Roman"" w:cs=""Times New Roman""/><w:sz w:val=""24""/><w:szCs w:val=""24""/><w:lang w:val=""ro-RO"" w:eastAsia=""ro-RO"" w:bidi=""ar-SA""/></w:rPr>";
        const string rPrBold = @"<w:rPr><w:rFonts w:ascii=""Times New Roman"" w:hAnsi=""Times New Roman"" w:cs=""Times New Roman""/><w:b/><w:sz w:val=""24""/><w:szCs w:val=""24""/><w:lang w:val=""ro-RO"" w:eastAsia=""ro-RO"" w:bidi=""ar-SA""/></w:rPr>";

        string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');

        foreach (var rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.Append($@"<w:p><w:pPr><w:jc w:val=""both""/><w:spacing w:before=""0"" w:after=""0"" w:line=""276"" w:lineRule=""auto""/></w:pPr><w:r>{rPrBase}</w:r></w:p>");
                continue;
            }

            string trimmed = line.TrimStart();

            // Detect section headers (e.g. CRANIO-CEREBRAL:, TORACE:, TEP:, ABDOMINO-PELVIN:, CONCLUZII:, etc.)
            bool isHeader = trimmed.StartsWith("CRANIO", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("TORACE", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("TEP", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("ABDOMIN", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("PELVIS", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("TRAUMA", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("COLOANA", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("CONCLUZII", StringComparison.OrdinalIgnoreCase) ||
                            (trimmed.EndsWith(":") && trimmed == trimmed.ToUpperInvariant() && trimmed.Length <= 45);

            string rPr = isHeader ? rPrBold : rPrBase;
            string escaped = SecurityElement.Escape(line);

            sb.Append($@"<w:p><w:pPr><w:jc w:val=""both""/><w:spacing w:before=""0"" w:after=""0"" w:line=""276"" w:lineRule=""auto""/></w:pPr><w:r>{rPr}<w:t xml:space=""preserve"">{escaped}</w:t></w:r></w:p>");
        }
    }
}

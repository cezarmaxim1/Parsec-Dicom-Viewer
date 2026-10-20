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
    /// formatted in Times New Roman 12pt with proper clinical indentation and header alignment.
    /// </summary>
    public static void SaveTextToDocx(string text, string targetFilePath, string title = "Raport Medical")
    {
        if (File.Exists(targetFilePath))
        {
            File.Delete(targetFilePath);
        }

        using var zip = ZipFile.Open(targetFilePath, ZipArchiveMode.Create);

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

        // 4. word/styles.xml (Establishes default font Times New Roman 12pt and proofing language Romanian ro-RO)
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

        // 5. word/settings.xml (Theme font language set to Romanian ro-RO and modern Word mode)
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

            const string rPrBase = @"<w:rPr><w:rFonts w:ascii=""Times New Roman"" w:hAnsi=""Times New Roman"" w:cs=""Times New Roman""/><w:sz w:val=""24""/><w:szCs w:val=""24""/><w:lang w:val=""ro-RO"" w:eastAsia=""ro-RO"" w:bidi=""ar-SA""/></w:rPr>";
            const string rPrBold = @"<w:rPr><w:rFonts w:ascii=""Times New Roman"" w:hAnsi=""Times New Roman"" w:cs=""Times New Roman""/><w:b/><w:sz w:val=""24""/><w:szCs w:val=""24""/><w:lang w:val=""ro-RO"" w:eastAsia=""ro-RO"" w:bidi=""ar-SA""/></w:rPr>";

            // Top horizontal separator line (matches clinical header format)
            sb.Append(@"<w:p>
  <w:pPr>
    <w:pBdr>
      <w:bottom w:val=""single"" w:sz=""6"" w:space=""1"" w:color=""A0A0A0""/>
    </w:pBdr>
    <w:spacing w:before=""0"" w:after=""120"" w:line=""240"" w:lineRule=""auto""/>
  </w:pPr>
</w:p>");

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

            // Page properties (A4 size, standard margins)
            sb.Append(@"<w:sectPr>
  <w:pgSz w:w=""11906"" w:h=""16838""/>
  <w:pgMar w:top=""1440"" w:right=""1440"" w:bottom=""1440"" w:left=""1440"" w:header=""720"" w:footer=""720"" w:gutter=""0""/>
</w:sectPr>
</w:body>
</w:document>");

            writer.Write(sb.ToString());
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
}

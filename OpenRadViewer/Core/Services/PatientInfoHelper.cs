// @purpose: Helper for extracting patient demographic details (age, sex, birth date) from DICOM tags or Romanian CNP identifiers.
// @depends: System, System.Globalization
// @exports: PatientInfoHelper

using System;
using System.Globalization;
using System.Linq;

namespace OpenRadViewer.Core.Services;

public static class PatientInfoHelper
{
    /// <summary>
    /// Parses birth date, sex, and calculated age from a Romanian CNP (Cod Numeric Personal, 13 digits).
    /// </summary>
    public static (int? age, DateTime? birthDate, string? sex) ParseFromCnp(string? cnp)
    {
        if (string.IsNullOrWhiteSpace(cnp)) return (null, null, null);
        string cleaned = new string(cnp.Where(char.IsDigit).ToArray());
        if (cleaned.Length != 13) return (null, null, null);

        char s = cleaned[0];
        int century;
        string sex;
        switch (s)
        {
            case '1': century = 1900; sex = "M"; break;
            case '2': century = 1900; sex = "F"; break;
            case '3': century = 1800; sex = "M"; break;
            case '4': century = 1800; sex = "F"; break;
            case '5': century = 2000; sex = "M"; break;
            case '6': century = 2000; sex = "F"; break;
            case '7': century = 1900; sex = "M"; break;
            case '8': century = 1900; sex = "F"; break;
            default: return (null, null, null);
        }

        if (!int.TryParse(cleaned.Substring(1, 2), out int yearPart) ||
            !int.TryParse(cleaned.Substring(3, 2), out int monthPart) ||
            !int.TryParse(cleaned.Substring(5, 2), out int dayPart))
        {
            return (null, null, null);
        }

        int fullYear = century + yearPart;
        try
        {
            var birthDate = new DateTime(fullYear, monthPart, dayPart);
            var today = DateTime.Today;
            int age = today.Year - birthDate.Year;
            if (birthDate.Date > today.AddYears(-age)) age--;

            if (age >= 0 && age <= 130)
            {
                return (age, birthDate, sex);
            }
        }
        catch
        {
            // Invalid date (e.g. bad month/day in non-CNP 13 digit number)
        }

        return (null, null, null);
    }

    /// <summary>
    /// Computes age from a DICOM birthdate string (e.g. YYYYMMDD).
    /// </summary>
    public static int? ParseAgeFromDicomDate(string? birthDateStr)
    {
        if (string.IsNullOrWhiteSpace(birthDateStr)) return null;
        string cleaned = new string(birthDateStr.Where(char.IsDigit).ToArray());
        if (cleaned.Length >= 8 &&
            DateTime.TryParseExact(cleaned.Substring(0, 8), "yyyyMMdd", 
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            var today = DateTime.Today;
            int age = today.Year - dt.Year;
            if (dt.Date > today.AddYears(-age)) age--;
            if (age >= 0 && age <= 130) return age;
        }
        return null;
    }

    /// <summary>
    /// Parses numeric age from standard DICOM Patient Age string (e.g. "044Y", "44Y", "44").
    /// </summary>
    public static int? ParseAgeFromDicomAgeString(string? ageStr)
    {
        if (string.IsNullOrWhiteSpace(ageStr)) return null;
        string s = ageStr.Trim();
        if (s.EndsWith("Y", StringComparison.OrdinalIgnoreCase))
        {
            string num = s.Substring(0, s.Length - 1).TrimStart('0');
            if (int.TryParse(string.IsNullOrEmpty(num) ? "0" : num, out int y) && y >= 0 && y <= 130)
                return y;
        }
        if (int.TryParse(s, out int raw) && raw >= 0 && raw <= 130)
            return raw;
        return null;
    }

    /// <summary>
    /// Returns formatted patient ID and calculated age (e.g. "1820322046285 (44 ani)").
    /// </summary>
    public static string FormatIdAndAge(string? patientId, string? birthDateStr = null, string? patientAgeStr = null)
    {
        string idPart = patientId?.Trim() ?? string.Empty;

        // 1. Try Romanian CNP
        int? age = null;
        var cnpResult = ParseFromCnp(idPart);
        if (cnpResult.age != null)
        {
            age = cnpResult.age;
        }

        // 2. Try DICOM Birth Date (0010,0030)
        if (age == null)
        {
            age = ParseAgeFromDicomDate(birthDateStr);
        }

        // 3. Try DICOM Patient Age (0010,1010)
        string? customAgeDisplay = null;
        if (age == null && !string.IsNullOrWhiteSpace(patientAgeStr))
        {
            var parsed = ParseAgeFromDicomAgeString(patientAgeStr);
            if (parsed.HasValue)
            {
                age = parsed.Value;
            }
            else
            {
                string s = patientAgeStr.Trim();
                if (s.EndsWith("M", StringComparison.OrdinalIgnoreCase))
                {
                    string num = s.Substring(0, s.Length - 1).TrimStart('0');
                    customAgeDisplay = $"{num} luni";
                }
                else if (s.EndsWith("D", StringComparison.OrdinalIgnoreCase))
                {
                    string num = s.Substring(0, s.Length - 1).TrimStart('0');
                    customAgeDisplay = $"{num} zile";
                }
                else if (s.EndsWith("W", StringComparison.OrdinalIgnoreCase))
                {
                    string num = s.Substring(0, s.Length - 1).TrimStart('0');
                    customAgeDisplay = $"{num} săpt.";
                }
            }
        }

        string? ageDisplay = age.HasValue ? $"{age.Value} ani" : customAgeDisplay;

        if (!string.IsNullOrEmpty(idPart) && !string.IsNullOrEmpty(ageDisplay))
        {
            return $"{idPart} ({ageDisplay})";
        }
        if (!string.IsNullOrEmpty(idPart))
        {
            return idPart;
        }
        if (!string.IsNullOrEmpty(ageDisplay))
        {
            return $"({ageDisplay})";
        }

        return string.Empty;
    }

    /// <summary>
    /// Formats DICOM time string (e.g. "113425" or "113425.120000" or "11:34:25") to "HH:mm:ss".
    /// </summary>
    public static string FormatDicomTime(string? rawTime)
    {
        if (string.IsNullOrWhiteSpace(rawTime)) return string.Empty;
        string t = rawTime.Trim();
        if (t.Contains(':'))
        {
            var parts = t.Split(':');
            if (parts.Length >= 2)
            {
                string hh = parts[0].PadLeft(2, '0');
                string mm = parts[1].PadLeft(2, '0');
                string ss = parts.Length > 2 ? parts[2].Split('.')[0].PadLeft(2, '0') : "00";
                return $"{hh}:{mm}:{ss}";
            }
            return t;
        }

        string digits = new string(t.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length >= 6)
        {
            return $"{digits.Substring(0, 2)}:{digits.Substring(2, 2)}:{digits.Substring(4, 2)}";
        }
        if (digits.Length >= 4)
        {
            return $"{digits.Substring(0, 2)}:{digits.Substring(2, 2)}:00";
        }

        return t;
    }
}


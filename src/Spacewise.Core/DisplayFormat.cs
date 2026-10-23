using System.Globalization;

namespace Spacewise.Core;

// Unit rule (spec §10.2): binary units labelled КБ/МБ/ГБ/ТБ as in Explorer, decimal comma,
// non-breaking space before the unit, one decimal below 100, integers from 100, narrow-space thousands.
public static class DisplayFormat
{
    private const string Nbsp = "\u00A0";
    private static readonly string[] Units = ["КБ", "МБ", "ГБ", "ТБ", "ПБ"];
    private static readonly NumberFormatInfo Numbers = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = "\u202F",
        NumberGroupSizes = [3],
        NegativeSign = "−",
    };

    public static string Count(long value) => value.ToString("#,0", Numbers);

    public static string Size(long bytes)
    {
        if (Math.Abs(bytes) < 1024) return Count(bytes) + Nbsp + "байт";
        double value = bytes / 1024.0;
        int unit = 0;
        // Promote when rounding would print 1024 of the current unit.
        while (unit < Units.Length - 1 && Math.Abs(value) >= 1023.5)
        {
            value /= 1024;
            unit++;
        }
        double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        string number = Math.Abs(rounded) < 100
            ? rounded.ToString("0.0", Numbers)
            : Math.Round(value, MidpointRounding.AwayFromZero).ToString("#,0", Numbers);
        return number + Nbsp + Units[unit];
    }
}

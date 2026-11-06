using System.Globalization;
using System.Text.RegularExpressions;
using Monica.Tool.Extensions;

namespace Monica.ModelQuery.Utils;

internal static partial class TemporalValueParser
{

    #region TimeOnly

    private static readonly string[] _supportedTimeOnlyFormats =
        ["HH:mm:ss", "HHmm"];

    public static bool TryToTimeOnly(string value, out TimeOnly timeSpan)
    {
        if (TimeOnly.TryParseExact(value, _supportedTimeOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.None,
                out timeSpan))
        {
            return true;
        }
        if (TimeOnly.TryParse(value, out timeSpan))
        {
            return true;
        }
        return false;
    }


    #endregion

    #region TimeSpan


    [GeneratedRegex("""
                    ^(?<value>\d+(.\d+)?)(?<unit>[A-Za-z]+)$
                    """, RegexOptions.Compiled)]
    private static partial Regex TimeSpanRegex();

    public static bool TryToTimeSpanWithUnit(string value, out TimeSpan timeSpan)
    {
        TimeSpan? result = null;
        timeSpan = default;
        value = value.Trim();
        var regex = TimeSpanRegex();
        var match = regex.Match(value);
        if (!match.Success) return false;
        var num = match.Groups["value"].Value;
        var unit = match.Groups["unit"].Value;

        if (!num.Contains('.'))
        {
            if (int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                result = Create(number);
            }
        }
        else
        {
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                result = Create(number);
            }
        }
        timeSpan = result ?? default;
        return result != null;

        TimeSpan? Create(double number)
        {
            return unit switch
            {
                null => null,
                "s" => TimeSpan.FromSeconds(number),
                "min" => TimeSpan.FromMinutes(number),
                "h" => TimeSpan.FromHours(number),
                "d" => TimeSpan.FromDays(number),
                _ => null
            };
        }
    }
    #endregion
    #region DateOnly
    private static readonly string[] _supportedDateOnlyFormats =
        ["yyyy-MM-dd", "yyyyMMdd", "MMdd", "yyyy-MM-dd HH:mm:ss", "yyMMdd"];
    public static bool TryToDateOnly(string value, out DateOnly timeSpan)
    {
        if (DateOnly.TryParseExact(value, _supportedDateOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.None,
                out timeSpan))
        {
            return true;
        }
        if (DateOnly.TryParse(value, out timeSpan))
        {
            return true;
        }
        return false;
    }


    #endregion
    #region DateTime

    private static readonly string[] _supportedDateTimeFormats =
       ["yyyy-MM-dd", "yyyyMMdd", "MMdd", "yyyy-MM-dd HH:mm:ss", "yyMMdd"];
    /// <summary>
    /// Tries to parse a string into a <see cref="DateTime"/>.
    /// </summary>
    /// <param name="value">The raw input string.</param>
    /// <param name="dateTime">The parsed <see cref="DateTime"/> value when parsing succeeds.</param>
    /// <returns><c>true</c> when parsing succeeds; otherwise <c>false</c>.</returns>
    public static bool TryToDateTime(string value, out DateTime dateTime)
    {
        value = value.Trim();
        if (TryToDateTimeWithExp(value, out dateTime))
        {
            return true;
        }

        if (TryToDateTimeNormal(value, out dateTime))
        {
            return true;
        }

        return false;
    }
    private static bool TryToDateTimeNormal(string value, out DateTime dateTime)
    {
        dateTime = default;
        if (value == "now")
        {
            dateTime = DateTime.Now;
            return true;
        }

        if (DateTime.TryParse(value, out dateTime))
        {
            return true;
        }


        if (DateTime.TryParseExact(value, _supportedDateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTime))
        {
            return true;
        }

        return false;
    }
    private static bool TryToDateTimeWithExp(string value, out DateTime dateTime)
    {
        dateTime = default;
        if (HasMathExp(value))
        {
            foreach (var (str, op) in GetExpUnit(value))
            {
                if (op == null)
                {
                    if (!TryToDateTimeNormal(str.Trim(), out dateTime))
                    {
                        return false;
                    }
                    continue;
                }

                if (!TryToTimeSpanWithUnit(str, out var span)) return false;
                switch (op)
                {
                    case "+":
                        dateTime += span;
                        break;
                    case "-":
                        dateTime -= span;
                        break;
                }
            }

            return true;
        }

        return false;
    }
    [GeneratedRegex("""
                    (?<op>[\+\-])(?<value>[^\+\-]+)
                    """, RegexOptions.Compiled)]
    private static partial Regex MathExpRegex();
    private static IEnumerable<(string, string?)> GetExpUnit(string value)
    {
        var regex = MathExpRegex();
        var returnFirst = false;
        foreach (Match match in regex.Matches(value))
        {
            if (returnFirst != true)
            {
                yield return (value[..match.Index], null);
                returnFirst = true;
            }
            var op = match.Groups["op"].Value;
            var str = match.Groups["value"].Value;
            yield return (str, op);
        }
    }

    private static readonly List<string> _mathOperators = ["+", "-"];

    private static bool HasMathExp(string value)
    {
        if (value.ContainsAny(_mathOperators)) return true;
        return false;
    }


    #endregion


}

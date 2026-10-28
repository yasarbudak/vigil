namespace Vigil.Core;

/// <summary>Parses compact durations such as <c>90s</c>, <c>15m</c>, <c>1h</c>, <c>1h30m</c>, <c>2d</c>.</summary>
public static class DurationParser
{
    public static bool TryParse(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            return TryParseCore(text.Trim(), out duration);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseCore(string input, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var total = TimeSpan.Zero;
        var index = 0;

        while (index < input.Length)
        {
            var digitsStart = index;
            while (index < input.Length && char.IsAsciiDigit(input[index]))
            {
                index++;
            }

            if (index == digitsStart || index == input.Length ||
                !int.TryParse(input.AsSpan(digitsStart, index - digitsStart), out var amount))
            {
                return false;
            }

            var unit = char.ToLowerInvariant(input[index++]);
            TimeSpan part;
            switch (unit)
            {
                case 's': part = TimeSpan.FromSeconds(amount); break;
                case 'm': part = TimeSpan.FromMinutes(amount); break;
                case 'h': part = TimeSpan.FromHours(amount); break;
                case 'd': part = TimeSpan.FromDays(amount); break;
                default: return false;
            }

            total += part;
        }

        if (total <= TimeSpan.Zero)
        {
            return false;
        }

        duration = total;
        return true;
    }
}

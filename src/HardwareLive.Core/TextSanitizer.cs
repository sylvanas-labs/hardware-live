namespace HardwareLive.Core;

/// <summary>
/// Strips C0 control characters and DEL from untrusted display text. Real hardware data has
/// shown up with embedded CR/NUL bytes in sensor and hardware names, and notes.json is
/// arbitrary third-party input (docs/SPEC.md); this is the one place that stripping happens
/// so every caller (notes, sensor/hardware labels) gets identical treatment.
/// </summary>
internal static class TextSanitizer
{
    public static string StripControlCharacters(string value)
    {
        Span<char> buffer = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (c >= ' ' && c != '\u007f')
            {
                buffer[length++] = c;
            }
        }

        return new string(buffer[..length]);
    }
}

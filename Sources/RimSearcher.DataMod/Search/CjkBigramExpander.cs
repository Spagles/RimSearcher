using System.Text;

namespace RimSearcher.DataMod.Search;

internal static class CjkBigramExpander
{
    public static string Expand(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = new StringBuilder(text);
        int runStart = -1;

        for (int i = 0; i <= text.Length; i++)
        {
            bool isCjk = i < text.Length && IsCjkChar(text[i]);
            if (isCjk)
            {
                if (runStart < 0)
                    runStart = i;
                continue;
            }

            if (runStart < 0)
                continue;

            int runLength = i - runStart;
            if (runLength >= 2)
            {
                result.Append(' ');
                for (int j = runStart; j < i - 1; j++)
                {
                    result.Append(text[j]);
                    result.Append(text[j + 1]);
                    result.Append(' ');
                }
            }

            runStart = -1;
        }

        return result.ToString();
    }

    private static bool IsCjkChar(char character) =>
        character is >= '\u4E00' and <= '\u9FFF'
            or >= '\u3400' and <= '\u4DBF'
            || character >= 0x20000 && character <= 0x2A6DF;
}

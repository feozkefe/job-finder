namespace JobFinder.Services;

/// <summary>
/// Cheap stopword-based language detection for job postings where the
/// source doesn't tell us the language (company career boards).
/// </summary>
public static class TextLanguage
{
    private static readonly string[] GermanMarkers =
    [
        " der ", " die ", " das ", " und ", " für ", " mit ", " wir ", " du ",
        " dich ", " dein", " unser", " bei ", " als ", " oder ", " nicht ",
        " werden ", " sind ", " eine ", " einen ", "kenntnisse", "erfahrung",
        "entwickler", " sowie ", " bereich ", " aufgaben", " bewerbung",
    ];

    private static readonly string[] EnglishMarkers =
    [
        " the ", " and ", " you ", " we ", " with ", " for ", " our ", " are ",
        " will ", " have ", "experience", " skills", " team ", " work ",
        " your ", " about ", " what ", " who ",
    ];

    /// <summary>Returns "German", "English" or null when there is too little signal.</summary>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var t = " " + text.ToLowerInvariant() + " ";
        var de = GermanMarkers.Sum(m => CountOccurrences(t, m));
        var en = EnglishMarkers.Sum(m => CountOccurrences(t, m));
        de += text.Count(c => c is 'ä' or 'ö' or 'ü' or 'ß' or 'Ä' or 'Ö' or 'Ü') / 4;

        if (de + en < 3)
            return null;
        return de > en ? "German" : "English";
    }

    private static int CountOccurrences(string text, string sub)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(sub, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += sub.Length;
        }
        return count;
    }
}

using System;

namespace Transom.Core;

/// <summary>A little delight: occasionally returns a cheerful message to show after an action.</summary>
public static class Encouragement
{
    private static readonly Random Rng = new();

    private static readonly string[] Messages =
    {
        "Nice work! 🎉",
        "Look at you, round-tripping schedules like a pro. 💪",
        "Spreadsheets fear you. 📊",
        "Another schedule wrangled. ✨",
        "Smooth. Very smooth. 😎",
        "Your BIM coordinator is smiling somewhere. 🙂",
        "Clean data is happy data. 🧼",
        "That's the good stuff. 👌",
        "You make Revit look easy. 🏗️",
        "Keep 'em coming — you're on a roll. 🎳",
        "Schedules: managed. Day: improved. ☀️",
        "Crisp. Tidy. Excellent. ✅",
        "Somewhere, a drawing set just got better. 📐",
        "Big 'gets things done' energy. ⚡",
        "Treat yourself to a coffee — you've earned it. ☕",
    };

    /// <summary>Roughly a 1-in-4 chance to return an encouraging line; otherwise null (stay quiet).</summary>
    public static string? Maybe()
    {
        if (Rng.Next(4) != 0) return null;
        return Messages[Rng.Next(Messages.Length)];
    }
}

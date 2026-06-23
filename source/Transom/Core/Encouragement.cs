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
        "Round-trip complete. Excel and Revit are friends again. 🤝",
        "Parameters, wrangled. 🪢",
        "That's one less thing on the punch list. 📋",
        "Effortless. (It wasn't, but it looked it.) 🎩",
        "Data went out, data came back, everybody's happy. 🔁",
        "QA/QC is going to love you for this. 🔍",
        "Type marks everywhere are rejoicing. 🏷️",
        "Future-you just saved a bunch of clicks. 🖱️",
        "Coordinated and accounted for. 🎯",
        "No more copy-paste gymnastics. 🤸",
        "Schedule synced. Sanity preserved. 🧘",
        "You and Transom make a great team. 🚪",
        "Tidy schedule, tidy mind. 🧠",
        "The model thanks you for your service. 🙏",
        "Deadlines tremble at your efficiency. ⏱️",
        "Pixel-perfect and parameter-perfect. 🎨",
        "Go on, take the win. 🏆",
        "Quietly excellent, as usual. 🤫",
        "Another one in the books. 📚",
        "Worksharing harmony achieved. 🎶",
        "Stretch break? You earned ten seconds. 🙆",
    };

    /// <summary>Roughly a 1-in-4 chance to return an encouraging line; otherwise null (stay quiet).</summary>
    public static string? Maybe()
    {
        if (Rng.Next(4) != 0) return null;
        return Messages[Rng.Next(Messages.Length)];
    }
}

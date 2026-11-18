namespace Plyph.Core;

public sealed record ActionTemplate(string Name, string Description, string Icon, string Prompt);

public static class ActionLibrary
{
    public static IReadOnlyList<ActionTemplate> Templates { get; } =
    [
        new("Summarize", "Condense text into its main points.", "summary",
            "Summarize the selected text in a short paragraph. Keep its main points and important qualifications. Do not introduce new facts."),
        new("Turn into bullets", "Make dense text easier to scan.", "list",
            "Convert the selected text into a concise bulleted list. Keep the original meaning and order where useful. Return only the list."),
        new("Translate", "Translate while preserving tone and meaning.", "language",
            "Translate the selected text into ${language}. If that language is unspecified, translate into English. Preserve the meaning, tone, and formatting. Return only the translation."),
        new("Draft a reply", "Respond to a message in a clear, friendly tone.", "mail",
            "Draft a brief, friendly reply to the selected message. Use only information present in the text. Do not invent commitments, dates, or personal details. Return only the reply."),
        new("Extract action items", "Find tasks, owners, and deadlines in notes.", "calendar",
            "List the action items in the selected text. Include an owner or deadline only when stated. If there are no action items, say so. Return only the list or that statement."),
        new("Explain in plain language", "Make a difficult passage easier to understand.", "idea",
            "Explain the selected text in plain language. Briefly define necessary technical terms and preserve important caveats. Return only the explanation."),
        new("Make more concise", "Remove repetition while keeping the meaning.", "format",
            "Shorten the selected text by removing repetition and unnecessary words. Preserve its meaning, tone, and important details. Return only the revised text."),
        new("Explain code", "Describe what selected code does.", "code",
            "Explain what the selected code does, including important inputs and outputs. Call out any behavior that cannot be determined from the snippet. Return only the explanation.")
    ];
}

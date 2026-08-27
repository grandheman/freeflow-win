namespace FreeFlow.Core.Context;

/// <summary>
/// What the foreground application looked like when dictation started.
/// </summary>
/// <remarks>
/// Any member may be null. On Windows, applications that do not expose a UI
/// Automation tree yield no selected text at all, so missing values are the normal
/// case rather than an error.
/// </remarks>
public sealed record AppSelectionSnapshot(
    string? AppName,
    string? ApplicationId,
    string? WindowTitle,
    string? SelectedText)
{
    public static readonly AppSelectionSnapshot Empty = new(null, null, null, null);

    public bool HasSelectedText => !string.IsNullOrWhiteSpace(SelectedText);
}

/// <summary>
/// The synthesized context handed to the cleanup prompt.
/// </summary>
/// <remarks>Ported from the <c>DictationContext</c> struct in <c>Sources/AppContextService.swift</c>.</remarks>
public sealed record DictationContext(
    string? AppName,
    string? ApplicationId,
    string? WindowTitle,
    string? SelectedText,
    string CurrentActivity,
    string? ContextSystemPrompt,
    string? ContextPrompt,
    string? ScreenshotDataUrl,
    string? ScreenshotMimeType,
    string? ScreenshotError)
{
    /// <summary>The single string interpolated into the cleanup prompt's CONTEXT field.</summary>
    public string ContextSummary => CurrentActivity;

    public static readonly DictationContext Empty = new(
        null, null, null, null, string.Empty, null, null, null, null, null);
}

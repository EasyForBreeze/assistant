using Microsoft.AspNetCore.Html;

namespace Assistant.Pages.Shared;

public enum FlashToastType
{
    Success,
    Error
}

public sealed class FlashToastModel
{
    public FlashToastType Type { get; init; }

    public string? Message { get; init; }

    public IHtmlContent? MessageHtml { get; init; }

    public string? Id { get; init; }

    public int TimeoutMs { get; init; } = 10_000;

    public bool IncludeValidationSummary { get; init; }
}

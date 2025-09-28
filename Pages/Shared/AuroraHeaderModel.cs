using System;
using Microsoft.AspNetCore.Html;

namespace Assistant.Pages.Shared;

public class AuroraHeaderModel
{
    public string Title { get; set; } = string.Empty;

    public string HeadingTag { get; set; } = "h3";

    public string ThemeClass { get; set; } = "aurora-green-indigo";

    public string Classes { get; set; } = "p-5 md:p-6 relative overflow-hidden mb-5";

    public string ContentClasses { get; set; } = "relative flex flex-col gap-4 md:flex-row md:items-center";

    public string TitleCssClass { get; set; } = "text-3xl md:text-4xl text-white tracking-tight drop-shadow-[0_6px_18px_rgba(99,102,241,0.25)]";

    public Func<dynamic, IHtmlContent>? TitleLeadContent { get; set; }

    public Func<dynamic, IHtmlContent>? LeadContent { get; set; }

    public string? Subtitle { get; set; }

    public Func<dynamic, IHtmlContent>? SubtitleContent { get; set; }

    public string SubtitleCssClass { get; set; } = "text-slate-200/80 mt-2 text-sm max-w-xl";

    public Func<dynamic, IHtmlContent>? RightContent { get; set; }

    public string RightContainerClasses { get; set; } = "flex items-center gap-3 w-full md:w-auto";

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle) || SubtitleContent != null;
}

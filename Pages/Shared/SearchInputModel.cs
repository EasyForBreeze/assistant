namespace Assistant.Pages.Shared;

public class SearchInputModel
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string Placeholder { get; set; } = string.Empty;

    public string? Value { get; set; }

    public int? MinLength { get; set; }

    public bool IconAbsolute { get; set; }

    public string WidthClasses { get; set; } = "w-full";

    public string ContainerClasses { get; set; } = string.Empty;

    public string InputClasses { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string LabelCssClass { get; set; } = "sr-only";

    public string InputType { get; set; } = "search";
}

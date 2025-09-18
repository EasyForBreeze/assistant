using System;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Assistant.Services;

public sealed class ConfluenceTemplateProvider
{
    private readonly Lazy<TemplatePayload> _template;

    public ConfluenceTemplateProvider(IWebHostEnvironment environment)
    {
        _template = new Lazy<TemplatePayload>(() => LoadTemplate(environment));
    }

    public TemplatePayload Template => _template.Value;

    private static TemplatePayload LoadTemplate(IWebHostEnvironment environment)
    {
        var path = Path.Combine(environment.ContentRootPath, "wiki.txt");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Wiki template not found at '{path}'.");
        }

        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var title = root.TryGetProperty("title", out var titleElement)
            ? titleElement.GetString() ?? string.Empty
            : string.Empty;

        var body = root
            .GetProperty("body")
            .GetProperty("storage")
            .GetProperty("value")
            .GetString() ?? string.Empty;

        return new TemplatePayload(title, body);
    }

    public sealed record TemplatePayload(string Title, string Body);
}

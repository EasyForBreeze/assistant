using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Assistant.Services;

public sealed class ConfluenceWikiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfluenceTemplateProvider _templateProvider;
    private readonly ConfluenceOptions _options;
    private readonly ILogger<ConfluenceWikiService> _logger;

    public ConfluenceWikiService(
        IHttpClientFactory httpClientFactory,
        ConfluenceTemplateProvider templateProvider,
        ConfluenceOptions options,
        ILogger<ConfluenceWikiService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _templateProvider = templateProvider;
        _options = options;
        _logger = logger;
    }

    public async Task CreatePageAsync(ClientWikiPayload payload, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogDebug("Confluence wiki connection is not configured. Skipping page creation for {ClientId}.", payload.ClientId);
            return;
        }

        try
        {
            var template = _templateProvider.Template;
            var html = BuildHtml(template.Body, payload);
            var title = BuildTitle(template.Title, payload.ClientId);

            using var request = JsonContent.Create(new
            {
                type = "page",
                title,
                space = new { key = _options.SpaceKey },
                ancestors = new[] { new { id = _options.ParentPageId } },
                body = new
                {
                    storage = new
                    {
                        value = html,
                        representation = "storage"
                    }
                }
            }, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var client = _httpClientFactory.CreateClient("confluence-wiki");
            PrepareClient(client);

            using var response = await client.PostAsync("/rest/api/content", request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var message = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogError(
                    "Failed to create Confluence page for {ClientId}. StatusCode: {Status}. Response: {Response}",
                    payload.ClientId,
                    response.StatusCode,
                    message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while creating Confluence page for {ClientId}.", payload.ClientId);
        }
    }

    private void PrepareClient(HttpClient client)
    {
        if (!_options.IsConfigured)
        {
            return;
        }

        if (client.BaseAddress is null && Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var uri))
        {
            client.BaseAddress = uri;
        }

        if (client.DefaultRequestHeaders.Authorization is null)
        {
            var credentials = Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(credentials));
        }
    }

    private static string BuildTitle(string templateTitle, string clientId)
    {
        if (!string.IsNullOrWhiteSpace(templateTitle))
        {
            var adjusted = templateTitle.Replace("ClientID", clientId, StringComparison.OrdinalIgnoreCase);
            adjusted = adjusted.Replace("Шаблон", "Конфигурация", StringComparison.OrdinalIgnoreCase);
            adjusted = adjusted.Replace("(не заполенный)", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(adjusted))
            {
                return adjusted;
            }
        }

        return $"Конфигурация клиента {clientId}";
    }

    private static string BuildHtml(string template, ClientWikiPayload payload)
    {
        var replacements = new Dictionary<string, string>
        {
            ["{{INFO_SYSTEM_CELL}}"] = BuildInfoSystemCell(payload),
            ["{{REALM_CELL}}"] = BuildRealmCell(payload.Realm),
            ["{{CLIENT_ID}}"] = WebUtility.HtmlEncode(payload.ClientId),
            ["{{CLIENT_NAME}}"] = WebUtility.HtmlEncode(payload.ClientName),
            ["{{ACCESS_TYPE}}"] = WebUtility.HtmlEncode(payload.AccessType),
            ["{{SERVICE_OWNER_CELL}}"] = BuildPersonCell(payload.ServiceOwner),
            ["{{SERVICE_MANAGER_CELL}}"] = BuildPersonCell(payload.ServiceManager),
            ["{{REDIRECT_TABLE}}"] = BuildRedirectTable(payload.RedirectUris),
            ["{{LOCAL_ROLES_TABLE}}"] = BuildLocalRolesTable(payload.LocalRoles),
            ["{{SERVICE_ROLES_TABLE}}"] = BuildServiceRolesTable(payload.ServiceRoles)
        };

        var result = template;
        foreach (var pair in replacements)
        {
            result = result.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        }

        return result;
    }

    private static string BuildInfoSystemCell(ClientWikiPayload payload)
    {
        var name = !string.IsNullOrWhiteSpace(payload.AppName)
            ? WebUtility.HtmlEncode(payload.AppName)
            : WebUtility.HtmlEncode(payload.ClientName);

        if (!string.IsNullOrWhiteSpace(payload.AppUrl))
        {
            return $"<td><a href=\"{WebUtility.HtmlEncode(payload.AppUrl)}\">{name}</a></td>";
        }

        return $"<td>{name}</td>";
    }

    private static string BuildRealmCell(string realm)
        => $"<td>{WebUtility.HtmlEncode(realm)}</td>";

    private static string BuildPersonCell(string? name)
    {
        var display = string.IsNullOrWhiteSpace(name)
            ? "—"
            : WebUtility.HtmlEncode(name);
        return $"<td><div class=\"content-wrapper\"><p>{display}</p></div></td>";
    }

    private static string BuildRedirectTable(IReadOnlyList<string> redirects)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["TEST"] = new(),
            ["STAGE"] = new(),
            ["PROD"] = new()
        };

        foreach (var uri in redirects)
        {
            var bucket = DetermineEnvironment(uri);
            grouped[bucket].Add(WebUtility.HtmlEncode(uri));
        }

        var sb = new StringBuilder();
        sb.Append("<p class=\"auto-cursor-target\"><br /></p>");
        sb.Append("<table class=\"wrapped\" data-mce-resize=\"false\"><colgroup><col /><col /><col /></colgroup><tbody>");
        sb.Append("<tr><th scope=\"col\"><span style=\"color:var(--ds-text-accent-blue-bolder,#09326c);\">TEST</span></th>");
        sb.Append("<th scope=\"col\"><span style=\"color:var(--ds-text-accent-blue-bolder,#09326c);\">STAGE</span></th>");
        sb.Append("<th scope=\"col\"><span style=\"color:var(--ds-text-accent-blue-bolder,#09326c);\">PROD</span></th></tr>");
        sb.Append("<tr>");

        foreach (var key in new[] { "TEST", "STAGE", "PROD" })
        {
            sb.Append("<td>");
            var entries = grouped[key];
            if (entries.Count == 0)
            {
                sb.Append("—");
            }
            else
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append("<br />");
                    }

                    sb.Append("<span class=\"nolink\">");
                    sb.Append(entries[i]);
                    sb.Append("</span>");
                }
            }

            sb.Append("</td>");
        }

        sb.Append("</tr></tbody></table>");
        return sb.ToString();
    }

    private static string DetermineEnvironment(string uri)
    {
        var value = uri.ToLowerInvariant();
        if (value.Contains("prod", StringComparison.Ordinal))
        {
            return "PROD";
        }

        if (value.Contains("stage", StringComparison.Ordinal)
            || value.Contains("stg", StringComparison.Ordinal)
            || value.Contains("preprod", StringComparison.Ordinal))
        {
            return "STAGE";
        }

        return "TEST";
    }

    private static string BuildLocalRolesTable(IReadOnlyList<string> roles)
    {
        var sb = new StringBuilder();
        sb.Append("<p class=\"auto-cursor-target\"><br /></p>");
        sb.Append("<table class=\"wrapped\" data-mce-resize=\"false\"><colgroup><col /><col /><col /></colgroup><tbody>");
        sb.Append("<tr><th scope=\"col\"><span style=\"color:var(--ds-text,#333333);\">Role Name</span></th>");
        sb.Append("<th scope=\"col\"><span style=\"color:var(--ds-text,#333333);\">Description</span></th>");
        sb.Append("<th scope=\"col\"><span style=\"color:var(--ds-text,#333333);\">Контур</span></th></tr>");

        if (roles.Count == 0)
        {
            sb.Append("<tr><td colspan=\"3\">—</td></tr>");
        }
        else
        {
            foreach (var role in roles)
            {
                sb.Append("<tr><td>");
                sb.Append(WebUtility.HtmlEncode(role));
                sb.Append("</td><td>—</td><td>—</td></tr>");
            }
        }

        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    private static string BuildServiceRolesTable(IReadOnlyList<(string ClientId, string Role)> serviceRoles)
    {
        var sb = new StringBuilder();
        sb.Append("<p class=\"auto-cursor-target\"><br /></p>");
        sb.Append("<table class=\"wrapped\" data-mce-resize=\"false\"><colgroup><col /><col /><col /></colgroup><tbody>");
        sb.Append("<tr><th scope=\"col\"><span style=\"color:var(--ds-text,#333333);\">Role Name</span></th>");
        sb.Append("<th scope=\"col\"><span style=\"color:var(--ds-text,#333333);\">Client</span></th>");
        sb.Append("<th scope=\"col\"><span style=\"color:var(--ds-text,#333333);\">Контур</span></th></tr>");

        if (serviceRoles.Count == 0)
        {
            sb.Append("<tr><td colspan=\"3\">—</td></tr>");
        }
        else
        {
            foreach (var (clientId, role) in serviceRoles)
            {
                sb.Append("<tr><td>");
                sb.Append(WebUtility.HtmlEncode(role));
                sb.Append("</td><td>");
                sb.Append(WebUtility.HtmlEncode(clientId));
                sb.Append("</td><td>—</td></tr>");
            }
        }

        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    public sealed record ClientWikiPayload(
        string Realm,
        string ClientId,
        string ClientName,
        string AccessType,
        IReadOnlyList<string> RedirectUris,
        IReadOnlyList<string> LocalRoles,
        IReadOnlyList<(string ClientId, string Role)> ServiceRoles,
        string? AppName,
        string? AppUrl,
        string? ServiceOwner,
        string? ServiceManager);
}

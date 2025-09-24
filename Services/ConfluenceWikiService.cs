using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Linq;
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
            var title = BuildTitle(template.Title, payload.ClientId,payload.Realm);

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
                return;
            }

            if (_options.Labels.Count == 0)
            {
                return;
            }

            var pageId = await TryExtractPageIdAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pageId))
            {
                _logger.LogWarning(
                    "Created Confluence page for {ClientId}, but the page id could not be determined to add labels.",
                    payload.ClientId);
                return;
            }

            await AddLabelsAsync(client, pageId, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while creating Confluence page for {ClientId}.", payload.ClientId);
        }
    }

    private async Task AddLabelsAsync(HttpClient client, string pageId, ClientWikiPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            using var request = JsonContent.Create(
                _options.Labels.Select(label => new { prefix = "global", name = label }).ToArray(),
                options: new JsonSerializerOptions(JsonSerializerDefaults.Web));

            using var response = await client.PostAsync($"/rest/api/content/{pageId}/label", request, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                _logger.LogDebug(
                    "Confluence labels already exist for page {PageId} ({ClientId}). Response: {Response}",
                    pageId,
                    payload.ClientId,
                    body);
                return;
            }

            _logger.LogError(
                "Failed to add labels to Confluence page {PageId} for {ClientId}. StatusCode: {Status}. Response: {Response}",
                pageId,
                payload.ClientId,
                response.StatusCode,
                body);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error while adding labels to Confluence page {PageId} for {ClientId}.",
                pageId,
                payload.ClientId);
        }
    }

    private static async Task<string?> TryExtractPageIdAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (document.RootElement.TryGetProperty("id", out var idProperty))
            {
                var id = idProperty.GetString();
                return string.IsNullOrWhiteSpace(id) ? null : id;
            }
        }
        catch (Exception)
        {
            // Ignore parsing errors and let the caller handle the missing id scenario.
        }

        return null;
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

    private static string BuildTitle(string templateTitle, string clientId,string realm)
    {
        if (!string.IsNullOrWhiteSpace(templateTitle))
        {
            var adjusted = templateTitle.Replace("ClientID", clientId, StringComparison.OrdinalIgnoreCase);
            if (realm!= "internal-bank-idm")
            {
                realm.Replace("INT-BNK.", "EXT-BNK.", StringComparison.OrdinalIgnoreCase);
            }
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
            ["{{CLIENT_NAME}}"] = WebUtility.HtmlEncode(BuildClientName(payload.ClientId)),
            ["{{ACCESS_TYPE}}"] = WebUtility.HtmlEncode(payload.ClientAuthEnabled ? "confidential" : "public"),
            ["{{DESCRIPTION}}"] = BuildDescription(payload.Description),
            ["{{SERVICE_OWNER_CELL}}"] = BuildPersonCell(payload.ServiceOwner),
            ["{{SERVICE_MANAGER_CELL}}"] = BuildPersonCell(payload.ServiceManager),
            ["{{STANDARD_FLOW}}"] = FormatToggle(payload.StandardFlowEnabled),
            ["{{DIRECT_ACCESS_GRANTS}}"] = FormatToggle(false),
            ["{{SERVICE_ACCOUNTS}}"] = FormatToggle(payload.ServiceAccountEnabled),
            ["{{DEVICE_AUTHORIZATION}}"] = FormatToggle(false),
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
            : WebUtility.HtmlEncode(BuildClientName(payload.ClientId));

        if (!string.IsNullOrWhiteSpace(payload.AppUrl))
        {
            return $"<td><a href=\"{WebUtility.HtmlEncode(payload.AppUrl)}\">{name}</a></td>";
        }

        return $"<td>{name}</td>";
    }

    private static string BuildRealmCell(string realm)
        => $"<td>{WebUtility.HtmlEncode(realm)}</td>";

    private static string BuildDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "—";
        }

        var normalized = description
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var encoded = WebUtility.HtmlEncode(normalized);
        return encoded.Replace("\n", "<br />", StringComparison.Ordinal);
    }

    private static string BuildPersonCell(string? name)
    {
        var display = string.IsNullOrWhiteSpace(name)
            ? "—"
            : WebUtility.HtmlEncode(name);
        return $"<td><div class=\"content-wrapper\"><p>{display}</p></div></td>";
    }

    private static string FormatToggle(bool enabled)
    {
        var state = enabled ? "On" : "Off";
        return $"<span style=\"color:var(--ds-text-accent-blue-bolder,#09326c);\">{state}</span>";
    }

    private static string BuildClientName(string clientId)
    {
        const string Prefix = "app-bank-";
        return clientId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? clientId[Prefix.Length..]
            : clientId;
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
            sb.Append("<td>");
            sb.Append("—");
            sb.Append("</td>");
            sb.Append("<td>");
            sb.Append("—");
            sb.Append("</td>");
            sb.Append("<td>");
            sb.Append("—");
            sb.Append("</td>");
            //sb.Append("<tr><td colspan=\"3\">—</td></tr>");
        }
        else
        {
            foreach (var role in roles)
            {
                sb.Append("<tr><td>");
                sb.Append(WebUtility.HtmlEncode(role));
                sb.Append("</td><td></td><td>TEST</td></tr>");
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
            sb.Append("<td>");
            sb.Append("—");
            sb.Append("</td>");
            sb.Append("<td>");
            sb.Append("—");
            sb.Append("</td>");
            sb.Append("<td>");
            sb.Append("—");
            sb.Append("</td>");
        }
        else
        {
            foreach (var (clientId, role) in serviceRoles)
            {
                sb.Append("<tr><td>");
                sb.Append(WebUtility.HtmlEncode(role));
                sb.Append("</td><td>");
                sb.Append(WebUtility.HtmlEncode(clientId));
                sb.Append("</td><td>TEST</td></tr>");
            }
        }

        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    public sealed record ClientWikiPayload(
        string Realm,
        string ClientId,
        string? Description,
        bool ClientAuthEnabled,
        bool StandardFlowEnabled,
        bool ServiceAccountEnabled,
        IReadOnlyList<string> RedirectUris,
        IReadOnlyList<string> LocalRoles,
        IReadOnlyList<(string ClientId, string Role)> ServiceRoles,
        string? AppName,
        string? AppUrl,
        string? ServiceOwner,
        string? ServiceManager);
}

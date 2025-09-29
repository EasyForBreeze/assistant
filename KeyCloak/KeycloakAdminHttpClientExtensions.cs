using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Assistant.KeyCloak;

internal static class KeycloakAdminHttpClientExtensions
{
    public static async Task<HttpResponseMessage> GetWithLegacyFallbackAsync(
        this HttpClient http,
        string modernUrl,
        string legacyUrl,
        CancellationToken ct)
        => await SendWithFallbackAsync(http, modernUrl, legacyUrl, static (client, url, token) => client.GetAsync(url, token), ct);

    public static async Task<HttpResponseMessage> PostJsonWithLegacyFallbackAsync<T>(
        this HttpClient http,
        string modernUrl,
        string legacyUrl,
        T body,
        JsonSerializerOptions serializerOptions,
        CancellationToken ct)
        => await SendWithFallbackAsync(http, modernUrl, legacyUrl,
            (client, url, token) => client.PostAsJsonAsync(url, body, serializerOptions, token), ct);

    public static async Task<HttpResponseMessage> PutJsonWithLegacyFallbackAsync<T>(
        this HttpClient http,
        string modernUrl,
        string legacyUrl,
        T body,
        JsonSerializerOptions serializerOptions,
        CancellationToken ct)
        => await SendWithFallbackAsync(http, modernUrl, legacyUrl,
            (client, url, token) => client.PutAsJsonAsync(url, body, serializerOptions, token), ct);

    public static async Task<HttpResponseMessage> DeleteJsonWithLegacyFallbackAsync<T>(
        this HttpClient http,
        string modernUrl,
        string legacyUrl,
        T body,
        JsonSerializerOptions serializerOptions,
        CancellationToken ct)
        => await SendWithFallbackAsync(http, modernUrl, legacyUrl,
            (client, url, token) => client.SendAsync(CreateDeleteRequest(url, body, serializerOptions), token), ct);

    public static async Task<HttpResponseMessage> PostWithLegacyFallbackAsync(
        this HttpClient http,
        string modernUrl,
        string legacyUrl,
        CancellationToken ct)
        => await SendWithFallbackAsync(http, modernUrl, legacyUrl,
            static (client, url, token) => client.PostAsync(url, content: null, token), ct);

    public static async Task<HttpResponseMessage> DeleteWithLegacyFallbackAsync(
        this HttpClient http,
        string modernUrl,
        string legacyUrl,
        CancellationToken ct)
        => await SendWithFallbackAsync(http, modernUrl, legacyUrl, static (client, url, token) => client.DeleteAsync(url, token), ct);

    public static async Task EnsureAdminSuccessAsync(this HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("Недостаточно прав для операции (нужны права realm-management).");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Запрос отклонён (400). Детали: {body}");
        }

        response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpResponseMessage> SendWithFallbackAsync(
        HttpClient http,
        string modernUrl,
        string legacyUrl,
        Func<HttpClient, string, CancellationToken, Task<HttpResponseMessage>> sender,
        CancellationToken ct)
    {
        var response = await sender(http, modernUrl, ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            return response;
        }

        response.Dispose();
        return await sender(http, legacyUrl, ct);
    }

    private static HttpRequestMessage CreateDeleteRequest<T>(
        string url,
        T body,
        JsonSerializerOptions serializerOptions)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url)
        {
            Content = JsonContent.Create(body, options: serializerOptions)
        };
        return request;
    }
}

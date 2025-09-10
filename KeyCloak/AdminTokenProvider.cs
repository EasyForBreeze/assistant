using Microsoft.Extensions.Options;

namespace Assistant.KeyCloak
{
    public interface IAdminTokenProvider
    {
        Task<string> GetAccessTokenAsync(CancellationToken ct = default);
    }

    internal sealed class AdminTokenProvider : IAdminTokenProvider
    {
        private readonly AdminApiOptions _opt;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private string? _token;
        private DateTimeOffset _expiresAt;

        public AdminTokenProvider(IOptions<AdminApiOptions> opt, IHttpClientFactory factory)
        {
            _opt = opt.Value;
            _http = factory.CreateClient(); // без имени; только для получения токена
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            // небольшой запас, чтобы не промахнуться по сроку
            if (!string.IsNullOrEmpty(_token) && DateTimeOffset.UtcNow < _expiresAt.AddSeconds(-30))
                return _token!;

            await _lock.WaitAsync(ct);
            try
            {
                if (!string.IsNullOrEmpty(_token) && DateTimeOffset.UtcNow < _expiresAt.AddSeconds(-30))
                    return _token!;

                var baseUrl = _opt.BaseUrl.TrimEnd('/');
                var realmPath = _opt.UseLegacyAuthPath ? "/auth/realms" : "/realms";
                var tokenUrl = $"{baseUrl}{realmPath}/{_opt.Realm}/protocol/openid-connect/token";

                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _opt.ClientId,
                    ["client_secret"] = _opt.ClientSecret
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
                {
                    Content = new FormUrlEncodedContent(form)
                };
                var resp = await _http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();

                var payload = await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct) ?? throw new InvalidOperationException("Invalid token response");
                _token = payload.access_token ?? throw new InvalidOperationException("No access_token");
                var lifetime = payload.expires_in > 0 ? payload.expires_in : 300;
                _expiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);
                return _token!;
            }
            finally { _lock.Release(); }
        }

        private sealed class TokenResponse
        {
            public string? access_token { get; set; }
            public int expires_in { get; set; }
            public string? token_type { get; set; }
        }
    }
}

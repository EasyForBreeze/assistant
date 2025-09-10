using System.Net.Http.Headers;

namespace Assistant.KeyCloak
{
    public sealed class AdminBearerHandler : DelegatingHandler
    {
        private readonly IAdminTokenProvider _tp;
        public AdminBearerHandler(IAdminTokenProvider tp) => _tp = tp;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = await _tp.GetAccessTokenAsync(ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, ct);
        }
    }
}

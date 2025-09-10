namespace Assistant.KeyCloak
{
    public sealed class AdminApiOptions
    {
        public string BaseUrl { get; set; } = default!;
        public string Realm { get; set; } = "master";
        public string ClientId { get; set; } = default!;
        public string ClientSecret { get; set; } = default!;
        // Для древних версий KC: /auth/realms вместо /realms
        public bool UseLegacyAuthPath { get; set; } = false;
    }
}

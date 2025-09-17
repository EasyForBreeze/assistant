using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Assistant
{
    public static class RoleRules
    {
        public const string LocalRolePrefix = "kc-gf-";
        public static readonly string[] ServiceClientPrefixes = { "app-bank-", "app-dom-" };
        private const int MaxRoleLength = 64;
        private const int TailMinLength = 2;
        private static readonly int TailMaxLength = MaxRoleLength - LocalRolePrefix.Length - 1;

        private static readonly Regex RoleNameRegex = new(
            $"^{Regex.Escape(LocalRolePrefix)}[a-z][a-z0-9._:-]{{{TailMinLength},{TailMaxLength}}}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex ServiceClientIdRegex = new(
            "^[a-z0-9][a-z0-9-]{2,60}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool IsValidRoleName(string? value)
        {
            var trimmed = value?.Trim();
            return !string.IsNullOrEmpty(trimmed) && RoleNameRegex.IsMatch(trimmed);
        }

        public static bool HasAllowedServiceClientPrefix(string? clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId)) return false;
            return ServiceClientPrefixes.Any(p => clientId.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsValidServiceClientId(string? clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId)) return false;
            if (!ServiceClientIdRegex.IsMatch(clientId)) return false;
            return HasAllowedServiceClientPrefix(clientId);
        }

        public static string FormatServiceClientPrefixes(string separator = " или ")
            => string.Join(separator, ServiceClientPrefixes.Select(p => $"'{p}'"));
    }
}


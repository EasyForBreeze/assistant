// Pages/Clients/Details.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Assistant.Pages.Clients
{
    public class DetailsModel : PageModel
    {
        // Параметры из query: ?realm=...&clientId=...
        [BindProperty(SupportsGet = true)] public string? Realm { get; set; }
        [BindProperty(SupportsGet = true)] public string? ClientId { get; set; }

        // То самое, что используется в cshtml: client?.ClientAuth и т.д.
        public ClientVm? Client { get; set; }

        public void OnGet()
        {
            // TODO: загрузить из Keycloak Admin API и замапить:
            // ClientAuth        = !publicClient
            // StandardFlow      = standardFlowEnabled
            // ServiceAccount    = serviceAccountsEnabled
            // Enabled           = enabled
            // Description       = description

            Client = new ClientVm
            {
                ClientId = ClientId ?? "app-bank-sample",
                Realm = Realm ?? "internal-bank-idm",
                Enabled = true,
                Description = "Description from Keycloak (stub)",
                ClientAuth = true,   // !publicClient
                StandardFlow = true,   // standardFlowEnabled
                ServiceAccount = false   // serviceAccountsEnabled
            };
        }

        public IActionResult OnPostSave()
        {
            // Здесь возьмёшь значения из Request.Form или через [BindProperty] на нужных полях.
            // Пока заглушка:
            TempData["Flash"] = "Changes saved (stub).";
            return RedirectToPage(new { realm = Realm ?? "internal-bank-idm", clientId = ClientId ?? "app-bank-sample" });
        }

        public IActionResult OnPostDelete()
        {
            // Заглушка удаления:
            TempData["Flash"] = "Client deleted (stub).";
            return RedirectToPage("/Index");
        }

        public class ClientVm
        {
            public string ClientId { get; set; } = default!;
            public string Realm { get; set; } = default!;
            public bool Enabled { get; set; }
            public string? Description { get; set; }

            // НУЖНЫЕ поля для Razor
            public bool ClientAuth { get; set; }   // конфиденциальный клиент?  = !publicClient
            public bool StandardFlow { get; set; }   // standardFlowEnabled
            public bool ServiceAccount { get; set; }   // serviceAccountsEnabled
        }
    }
}

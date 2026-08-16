using Microsoft.AspNetCore.Mvc;

namespace Library_Management_system.Controllers.Login
{
    public class LoginController : Controller
    {
        // The real sign-in form lives in the Identity area
        // (/Identity/Account/Login). This action only keeps the older
        // /Login/Index links working.
        public IActionResult Index(string? returnUrl = null)
        {
            return RedirectToPage("/Account/Login", new { area = "Identity", returnUrl });
        }
    }
}

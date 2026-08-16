// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Threading.Tasks;
using Library_Management_system.Models;
using Library_Management_system.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Library_Management_system.Areas.Identity.Pages.Account
{
    /// <summary>
    /// Sends a single-use password reset link by email.
    /// </summary>
    /// <remarks>
    /// This replaced a Telegram OTP flow that also collected the new password up front and held it
    /// in server memory until a code was confirmed. The token here is ASP.NET Identity's own: it is
    /// derived from the user's current security stamp, so it expires on its own schedule and stops
    /// working the moment the password changes. Nothing secret is cached.
    /// </remarks>
    [AllowAnonymous]
    public class ForgotPasswordModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IAccountEmailService _accountEmail;
        private readonly ILogger<ForgotPasswordModel> _logger;

        public ForgotPasswordModel(
            UserManager<ApplicationUser> userManager,
            IAccountEmailService accountEmail,
            ILogger<ForgotPasswordModel> logger)
        {
            _userManager = userManager;
            _accountEmail = accountEmail;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public class InputModel
        {
            [Required(ErrorMessage = "Enter the email address on your library account.")]
            [EmailAddress(ErrorMessage = "Enter a valid email address.")]
            [Display(Name = "Email")]
            public string Email { get; set; }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            var email = Input.Email.Trim();
            var user = await _userManager.FindByEmailAsync(email);

            // Always end on the same confirmation page whether or not the account exists. Telling
            // an anonymous visitor "no such account" would turn this form into a way to test which
            // email addresses are registered.
            if (user == null)
            {
                _logger.LogInformation("Password reset requested for an unknown address.");
                return RedirectToPage("./ForgotPasswordConfirmation");
            }

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);

            // The raw token contains characters that do not survive a query string intact.
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

            var resetUrl = Url.Page(
                "./ResetPassword",
                pageHandler: null,
                values: new { area = "Identity", code = encodedToken, email = user.Email },
                protocol: Request.Scheme);

            await _accountEmail.SendPasswordResetLinkAsync(user.Email, user.FullName, resetUrl);

            // Send fails are not surfaced either, for the same reason: a visible difference between
            // "sent" and "not sent" leaks whether the address is registered. The failure is logged.
            return RedirectToPage("./ForgotPasswordConfirmation");
        }
    }
}

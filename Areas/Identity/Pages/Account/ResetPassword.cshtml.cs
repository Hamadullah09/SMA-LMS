// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#nullable disable

using System;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Threading.Tasks;
using Library_Management_system.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace Library_Management_system.Areas.Identity.Pages.Account
{
    /// <summary>
    /// Accepts the emailed reset link and sets the new password.
    /// </summary>
    [AllowAnonymous]
    public class ResetPasswordModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;

        public ResetPasswordModel(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public class InputModel
        {
            [Required]
            [EmailAddress]
            public string Email { get; set; }

            [Required]
            public string Code { get; set; }

            [Required(ErrorMessage = "Enter a new password.")]
            [StringLength(100, ErrorMessage = "The {0} must be at least {2} and at most {1} characters long.", MinimumLength = 6)]
            [DataType(DataType.Password)]
            [Display(Name = "New password")]
            public string Password { get; set; }

            [Required(ErrorMessage = "Confirm the new password.")]
            [DataType(DataType.Password)]
            [Display(Name = "Confirm password")]
            [Compare(nameof(Password), ErrorMessage = "The password and confirmation password do not match.")]
            public string ConfirmPassword { get; set; }
        }

        public IActionResult OnGet(string code = null, string email = null)
        {
            // Arriving here without a link is a dead end — there is nothing to verify against, so
            // send the user back to request one rather than showing a form that cannot succeed.
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(email))
            {
                TempData["ErrorMessage"] = "That reset link is incomplete. Please request a new one.";
                return RedirectToPage("./ForgotPassword");
            }

            Input = new InputModel
            {
                Email = email,
                Code = code
            };

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            var user = await _userManager.FindByEmailAsync(Input.Email);
            if (user == null)
            {
                // Same reasoning as ForgotPassword: do not confirm which addresses exist.
                return RedirectToPage("./ResetPasswordConfirmation");
            }

            string token;
            try
            {
                token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(Input.Code));
            }
            catch (FormatException)
            {
                // A truncated or mangled link — mail clients sometimes wrap long URLs.
                ModelState.AddModelError(string.Empty,
                    "That reset link is not valid. It may have been broken across lines by your email app. Please request a new one.");
                return Page();
            }

            var result = await _userManager.ResetPasswordAsync(user, token, Input.Password);
            if (result.Succeeded)
            {
                return RedirectToPage("./ResetPasswordConfirmation");
            }

            foreach (var error in result.Errors)
            {
                // Identity reports an expired, already-used or tampered token as InvalidToken.
                // Rewritten because "Invalid token." does not tell anyone what to do next.
                if (string.Equals(error.Code, "InvalidToken", StringComparison.Ordinal))
                {
                    ModelState.AddModelError(string.Empty,
                        "This reset link has expired or has already been used. Please request a new one.");
                    continue;
                }

                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }
    }
}

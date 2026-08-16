#nullable disable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Library_Management_system.Models;
using Library_Management_system.Services; // PhoneNumberHelper — sign-in still accepts a phone number.
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Library_Management_system.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class LoginModel : PageModel
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<LoginModel> _logger;

        public LoginModel(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            ILogger<LoginModel> logger)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public IList<AuthenticationScheme> ExternalLogins { get; set; }
        public string ReturnUrl { get; set; }

        [TempData]
        public string ErrorMessage { get; set; }

        [TempData]
        public string InfoMessage { get; set; }

        public class InputModel
        {
            [Required]
            [Display(Name = "Email or Phone")]
            public string Email { get; set; }

            [Required]
            [DataType(DataType.Password)]
            public string Password { get; set; }

            [Display(Name = "Remember me?")]
            public bool RememberMe { get; set; }
        }

        public async Task OnGetAsync(string returnUrl = null)
        {
            if (!string.IsNullOrEmpty(ErrorMessage))
                ModelState.AddModelError(string.Empty, ErrorMessage);

            returnUrl ??= Url.Content("~/");
            ReturnUrl = returnUrl;

            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();
        }

        public async Task<IActionResult> OnPostAsync(string returnUrl = null)
        {
            returnUrl ??= Url.Content("~/");
            ReturnUrl = returnUrl;

            ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();

            if (!ModelState.IsValid)
                return Page();

            var loginIdentifier = (Input.Email ?? string.Empty).Trim();
            var user = await _userManager.FindByEmailAsync(loginIdentifier);
            if (user == null)
            {
                user = await FindUserByPhoneAsync(loginIdentifier);
            }

            if (user == null)
            {
                ModelState.AddModelError(string.Empty, "Invalid login attempt.");
                return Page();
            }

            var approvalStatus = await GetApprovalStatusAsync(user);
            if (approvalStatus == AccountApproval.Pending)
            {
                ModelState.AddModelError(string.Empty, "Your account is pending admin approval.");
                return Page();
            }

            if (approvalStatus == AccountApproval.Canceled)
            {
                ModelState.AddModelError(string.Empty, "Your account access was canceled by admin.");
                return Page();
            }

            var result = await _signInManager.CheckPasswordSignInAsync(
                user,
                Input.Password,
                lockoutOnFailure: false);

            if (result.Succeeded)
            {
                return await SignInAsync(user, returnUrl);
            }

            if (result.IsNotAllowed && !user.EmailConfirmed)
            {
                user.EmailConfirmed = true;
                var confirmResult = await _userManager.UpdateAsync(user);
                if (confirmResult.Succeeded)
                {
                    var retryResult = await _signInManager.CheckPasswordSignInAsync(
                        user,
                        Input.Password,
                        lockoutOnFailure: false);

                    if (retryResult.Succeeded)
                    {
                        return await SignInAsync(user, returnUrl);
                    }
                }
            }

            if (result.IsLockedOut)
            {
                ModelState.AddModelError(string.Empty, "Your account is currently locked. Please contact admin.");
                return Page();
            }

            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return Page();
        }

        // Sign-in is a single factor: the password checked by the caller. The second factor used
        // to be a Telegram OTP, which was removed along with the bot — password reset now proves
        // control of the account's email address instead.
        private async Task<IActionResult> SignInAsync(ApplicationUser user, string returnUrl)
        {
            await _signInManager.SignInAsync(user, Input.RememberMe);
            _logger.LogInformation("User {UserId} signed in.", user.Id);
            return await RedirectAfterSignInAsync(user, returnUrl);
        }

        private async Task<IActionResult> RedirectAfterSignInAsync(ApplicationUser user, string returnUrl)
        {
            if (await _userManager.IsInRoleAsync(user, "Admin") ||
                await _userManager.IsInRoleAsync(user, "Librarian"))
            {
                return Redirect("/admin/dashboard");
            }

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            return RedirectToAction("Index", "Home");
        }

        private async Task<ApplicationUser> FindUserByPhoneAsync(string rawPhoneNumber)
        {
            if (!PhoneNumberHelper.LooksLikePhone(rawPhoneNumber))
            {
                return null;
            }

            var candidates = PhoneNumberHelper.BuildMatchCandidates(rawPhoneNumber).ToArray();
            if (candidates.Length == 0)
            {
                return null;
            }

            var user = await _userManager.Users
                .FirstOrDefaultAsync(u => u.PhoneNumber != null && candidates.Contains(u.PhoneNumber));
            if (user != null)
            {
                return user;
            }

            var phoneCandidates = await _userManager.Users
                .Where(u => u.PhoneNumber != null)
                .ToListAsync();

            return phoneCandidates.FirstOrDefault(u => PhoneNumberHelper.AreEquivalent(u.PhoneNumber, rawPhoneNumber));
        }

        private async Task<string> GetApprovalStatusAsync(ApplicationUser user)
        {
            var claims = await _userManager.GetClaimsAsync(user);
            var rawValue = claims
                .FirstOrDefault(c => c.Type == AccountApproval.ClaimType)
                ?.Value;

            return AccountApproval.Normalize(rawValue);
        }
    }
}

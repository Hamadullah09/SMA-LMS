using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace Library_Management_system.Models
{
    public class ApplicationUser : IdentityUser
    {
        [Required]
        [StringLength(100)]
        public string FullName { get; set; } = string.Empty;

        [StringLength(100)]
        public string? CreatedBy { get; set; }

        public DateTime? CreatedDate { get; set; }

        // ResetPasswordToken/Expiry and the three Telegram columns were removed with the Telegram
        // OTP flow. Password reset now uses ASP.NET Identity's own token, which is derived from
        // the security stamp and never stored on the user row.
    }
}

using System.ComponentModel.DataAnnotations;
using Library_Management_system.Domain.Enums;

namespace Library_Management_system.Domain.Entities;

public class Department
{
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Code { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<AcademicProgram> Programs { get; set; } = new List<AcademicProgram>();
}

/// <summary>
/// Named AcademicProgram rather than Program to avoid colliding with the top-level
/// Program class generated for the web host.
/// </summary>
public class AcademicProgram
{
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Code { get; set; }

    /// <summary>Nominal length, used to sanity-check semester values.</summary>
    public int DurationSemesters { get; set; }

    public int DepartmentId { get; set; }
    public Department? Department { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// University identity, deliberately separate from ApplicationUser (authentication identity).
/// The two have different lifecycles: a student record can exist before a login does (bulk
/// import, specification section 35) and must survive account deactivation.
/// </summary>
public class Student
{
    public int Id { get; set; }

    /// <summary>Institutional student number.</summary>
    [Required, MaxLength(50)]
    public string StudentIdNumber { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string RollNumber { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string FullName { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? Email { get; set; }

    [MaxLength(30)]
    public string? Phone { get; set; }

    /// <summary>
    /// Sensitive personal data (specification section 44). Masked in all UI by default,
    /// never written to logs, full value readable only by Admin.
    /// </summary>
    [MaxLength(30)]
    public string? Cnic { get; set; }

    [MaxLength(400)]
    public string? PhotoPath { get; set; }

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public int? AcademicProgramId { get; set; }
    public AcademicProgram? AcademicProgram { get; set; }

    public int? Semester { get; set; }

    public StudentStatus Status { get; set; } = StudentStatus.Active;

    /// <summary>
    /// Link to the login account. Nullable so imported students exist before they first log in.
    /// </summary>
    [MaxLength(450)]
    public string? ApplicationUserId { get; set; }

    /// <summary>
    /// Set by an administrator to block borrowing for reasons outside the automatic policy
    /// checks (specification section 31). Automatic restrictions are computed, not stored.
    /// </summary>
    public bool IsBorrowingBlocked { get; set; }

    [MaxLength(400)]
    public string? BorrowingBlockReason { get; set; }

    [MaxLength(150)]
    public string? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedUtc { get; set; }

    public ICollection<StudentRfidTag> RfidTags { get; set; } = new List<StudentRfidTag>();

    /// <summary>Masked form for display, e.g. 42101-*******-7 (specification section 44).</summary>
    public string? MaskedCnic => MaskCnic(Cnic);

    public static string? MaskCnic(string? cnic)
    {
        if (string.IsNullOrWhiteSpace(cnic))
        {
            return null;
        }

        var trimmed = cnic.Trim();
        if (trimmed.Length <= 2)
        {
            return new string('*', trimmed.Length);
        }

        // Keep the leading block and the final check digit, mask everything between.
        var separatorIndex = trimmed.IndexOf('-');
        var prefix = separatorIndex > 0 ? trimmed[..separatorIndex] : trimmed[..Math.Min(5, trimmed.Length)];
        var suffix = trimmed[^1..];
        return $"{prefix}-*******-{suffix}";
    }
}

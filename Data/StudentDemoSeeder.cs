using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Data;

/// <summary>
/// A handful of student records so circulation can be exercised.
///
/// Without these there is nobody to issue a book to: <see cref="SampleDataSeeder"/> creates titles
/// only, and <c>Student</c> is a university identity that is normally imported from the registry
/// rather than created by signing up (specification section 35).
///
/// Deliberately does not create RFID cards. With real hardware attached, a synthetic EPC is a card
/// nobody can present to a reader, and it would turn every genuine enrolment into a "replace" of a
/// tag that never physically existed. Cards are enrolled by holding them against the antenna on the
/// tag assignment screen.
///
/// Development only — gated on the same switch as the sample catalogue.
/// </summary>
public static class StudentDemoSeeder
{
    private sealed record Sample(string Roll, string Name, string Email, int Semester);

    private static readonly Sample[] Students =
    [
        new("SMA-2026-001", "Ayesha Khan",      "ayesha.khan@example.edu",   3),
        new("SMA-2026-002", "Bilal Ahmed",      "bilal.ahmed@example.edu",   3),
        new("SMA-2026-003", "Fatima Noor",      "fatima.noor@example.edu",   5),
        new("SMA-2026-004", "Hamza Iqbal",      "hamza.iqbal@example.edu",   5),
        new("SMA-2026-005", "Zainab Malik",     "zainab.malik@example.edu",  1),
        new("SMA-2026-006", "Usman Raza",       "usman.raza@example.edu",    7),
        new("SMA-2026-007", "Maryam Siddiqui",  "maryam.siddiqui@example.edu", 1),
        new("SMA-2026-008", "Ali Hassan",       "ali.hassan@example.edu",    7),

        // Added so every card on the supplier sheet has a holder. Roll numbers continue the
        // sequence, which keeps the card importer's roll-order pairing stable: existing students
        // keep the cards they already have and these four take the spares.
        new("SMA-2026-009", "Sana Tariq",       "sana.tariq@example.edu",     3),
        new("SMA-2026-010", "Kashif Mehmood",   "kashif.mehmood@example.edu", 5),
        new("SMA-2026-011", "Areeba Javed",     "areeba.javed@example.edu",   1),
        new("SMA-2026-012", "Noman Aslam",      "noman.aslam@example.edu",    7)
    ];

    public static async Task SeedAsync(ApplicationDbContext context, CancellationToken ct = default)
    {
        var department = await EnsureDepartmentAsync(context, ct);

        var existing = await context.Students
            .Select(s => s.RollNumber)
            .ToListAsync(ct);

        var known = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var added = false;

        foreach (var sample in Students)
        {
            if (known.Contains(sample.Roll))
            {
                continue;
            }

            context.Students.Add(new Student
            {
                StudentIdNumber = sample.Roll,
                RollNumber = sample.Roll,
                FullName = sample.Name,
                Email = sample.Email,
                DepartmentId = department.Id,
                Semester = sample.Semester,
                Status = StudentStatus.Active,
                CreatedBy = "Demo Seed",
                CreatedUtc = DateTime.UtcNow
            });

            added = true;
        }

        if (added)
        {
            await context.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Ties a login account to a student record.
    ///
    /// These are separate identities by design — a student exists in the registry before they ever
    /// sign in (specification section 35) — but the link has to exist for anything that works from
    /// the signed-in user to find the borrower, such as the kiosk identifying a student who arrived
    /// from their own cart.
    /// </summary>
    public static async Task LinkAccountAsync(
        ApplicationDbContext context,
        string email,
        string applicationUserId,
        string fullName,
        CancellationToken ct = default)
    {
        var student = await context.Students.FirstOrDefaultAsync(s => s.Email == email, ct);

        if (student is null)
        {
            var department = await EnsureDepartmentAsync(context, ct);

            student = new Student
            {
                StudentIdNumber = "SMA-2026-100",
                RollNumber = "SMA-2026-100",
                FullName = fullName,
                Email = email,
                DepartmentId = department.Id,
                Semester = 1,
                Status = StudentStatus.Active,
                CreatedBy = "Demo Seed",
                CreatedUtc = DateTime.UtcNow
            };

            context.Students.Add(student);
        }

        if (student.ApplicationUserId == applicationUserId)
        {
            return;
        }

        student.ApplicationUserId = applicationUserId;
        await context.SaveChangesAsync(ct);
    }

    private static async Task<Department> EnsureDepartmentAsync(
        ApplicationDbContext context, CancellationToken ct)
    {
        var existing = await context.Departments.OrderBy(d => d.Id).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        var department = new Department
        {
            Name = "Computer Science",
            Code = "CS",
            IsActive = true
        };

        context.Departments.Add(department);
        await context.SaveChangesAsync(ct);

        return department;
    }
}

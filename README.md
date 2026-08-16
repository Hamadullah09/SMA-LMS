# Library Management System

Library Management System is an ASP.NET Core 9 web app for managing books, reservations, borrowing, returns, fines, and user accounts with role-based access.

## Core Features

- ASP.NET Core Identity authentication with email confirmation
- Role-based access (`Admin`, `Librarian`, `User`)
- Book catalog management (books, categories, authors, images)
- Reservation workflow with approval/rejection and FIFO priority checks
- Borrowing lifecycle (create, update, return) with fine tracking
- Student-facing pages for search, cart, bookmark, history, and reviews
- Admin reporting (`borrowing`, `returns`, `most-borrowed`, `fine-collection`)
- Contact inbox and feedback/review moderation
- Email password reset links, overdue reminders and admin alerts (SMTP)

## Tech Stack

| Layer         | Technology                           |
| ------------- | ------------------------------------ |
| Framework     | ASP.NET Core 9 (MVC + Razor Pages)   |
| Language      | C# / .NET 9                          |
| Data          | Entity Framework Core 9 + SQL Server |
| Identity      | ASP.NET Core Identity                |
| UI            | Razor Views, Bootstrap 5, JavaScript |
| Email         | MailKit (SMTP — Gmail app password)  |

## Project Structure

```text
.
|-- Controllers/
|   |-- Admin/
|   |-- User/
|   |-- AccountController.cs
|   |-- HomeController.cs
|-- Data/
|   |-- ApplicationDbContext.cs
|   |-- Migrations/
|-- Models/
|   |-- Admin/
|   |-- ApplicationUser.cs
|-- Services/
|   |-- EmailSender.cs
|   |-- AccountEmailService.cs
|-- Views/
|-- Areas/Identity/Pages/
|-- wwwroot/
|-- Program.cs
|-- appsettings.json
```

## Prerequisites

- .NET 9 SDK
- SQL Server (2019+ recommended)
- EF Core CLI: `dotnet tool install --global dotnet-ef`

## Quick Start

```bash
git clone https://github.com/Rattnakvisal/Library-Management-system.git
cd Library-Management-system
dotnet restore
dotnet build
dotnet ef database update
dotnet run --launch-profile https
```

Open:

- `https://localhost:7004`
- `http://localhost:5083`

## Configuration

Use `appsettings.Development.json` or user-secrets for local sensitive values.

### Required keys

```json
{
    "ConnectionStrings": {
        "DefaultConnection": "Server=YOUR_SERVER;Database=LIBRARY_DB;User ID=YOUR_USER;Password=YOUR_PASSWORD;TrustServerCertificate=True;"
    },
    "EmailSettings": {
        "SenderName": "SMA Library",
        "SenderEmail": "you@gmail.com",
        "SmtpServer": "smtp.gmail.com",
        "SmtpPort": "587",
        "Password": "YOUR_16_CHAR_APP_PASSWORD",
        "AdminEmail": "librarian@yourdomain.com"
    },
    "SeedAdmin": {
        "Email": "admin@library.com",
        "Password": "Admin@123",
        "ResetPasswordOnStartup": false
    }
}
```

### Gmail setup

Password reset links, overdue reminders and admin alerts all go out over SMTP, so email must
work for a student to be able to recover an account on their own.

Gmail rejects normal account passwords over SMTP. You need an **App password**:

1. Turn on 2-Step Verification at <https://myaccount.google.com/security>.
2. Create an app password at <https://myaccount.google.com/apppasswords>.
3. Put the 16-character value in `EmailSettings:Password` and the Gmail address in
   `EmailSettings:SenderEmail`.

`EmailSettings:AdminEmail` receives registration and approval alerts; it falls back to
`SeedAdmin:Email` when blank. Keep credentials in user-secrets rather than source control.

In Development with no SMTP configured, nothing is sent — the reset link is written to the
application log at Warning level instead, so the flow can still be completed while testing.
Production refuses to start without SMTP server, sender and password.

### User-secrets example

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=YOUR_SERVER;Database=LIBRARY_DB;User ID=YOUR_USER;Password=YOUR_PASSWORD;TrustServerCertificate=True;"
dotnet user-secrets set "EmailSettings:SenderEmail" "you@example.com"
dotnet user-secrets set "EmailSettings:Password" "your-app-password"
dotnet user-secrets set "SeedAdmin:Email" "admin@library.com"
dotnet user-secrets set "SeedAdmin:Password" "Admin@123"
```

## Database and Seeding

On startup, the app automatically:

- Applies pending EF Core migrations
- Ensures roles exist: `Admin`, `Librarian`, `User`
- Ensures a seed admin account exists and is in `Admin` role
- Optionally resets seed admin password in Development mode

Manual migration commands:

```bash
dotnet ef migrations add YourMigrationName
dotnet ef database update
dotnet ef migrations list
```

## Roles and Access

| Role      | Access                                                                   |
| --------- | ------------------------------------------------------------------------ |
| Admin     | Full management: users, books, category/author/event, reports, dashboard |
| Librarian | Operational management: dashboard, borrowing, catalog operations         |
| User      | Browse/search books, reserve via cart, bookmark, review, history/profile |

## Main Routes

| Area             | Route                        |
| ---------------- | ---------------------------- |
| Home             | `/`                          |
| Login            | `/login`                     |
| Book list        | `/book`                      |
| Book detail      | `/book/{id}`                 |
| Cart             | `/cart`                      |
| Bookmark         | `/bookmark`                  |
| History          | `/history`                   |
| Admin dashboard  | `/admin/dashboard`           |
| Manage users     | `/admin/manageuser`          |
| Manage borrowing | `/admin/manageborrowingbook` |
| Reports          | `/admin/managereport`        |

## Development Notes

- Default launch profile is configured in `Properties/launchSettings.json`
- Static assets are under `wwwroot/`
- Admin report data endpoint: `GET /admin/managereport/data`
- Fine calculation uses a fixed rate of `$1.00/day` in current logic

## Security Notes

- Do not commit real credentials/tokens in `appsettings.json`
- Prefer environment variables or `dotnet user-secrets` for local development
- Rotate any leaked connection strings, SMTP passwords, or bot tokens immediately

## Contributing

```bash
git checkout -b feature/your-feature
git commit -m "feat: your description"
git push origin feature/your-feature
```

Then open a pull request.

## License

MIT License - see [LICENSE](LICENSE).

## Contact

GitHub: [@Rattnakvisal](https://github.com/Rattnakvisal)

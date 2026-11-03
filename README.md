# Global School ERP

A multi-tenant school ERP built from the "Project Plan, Architecture & SRS" document: an ASP.NET Core (.NET 10) modular monolith with an Angular 20 front end.

This is a working first slice of the nine MVP modules plus year-end rollover. It is not the finished nine-month product. The [coverage table](#requirement-coverage) says exactly what is built, what is partial and what is missing.

## Run it

You need the .NET 10 SDK and Node.js 20.19+ (or 22.12+, 24+).

Terminal 1, the API (creates a SQLite database and demo data on first start):

```bash
dotnet run --project src/SchoolErp.Api
```

Terminal 2, the web app:

```bash
cd web
npm install
npm start
```

Open http://localhost:4200. The dev server proxies `/api` to the API on port 5080.

To reset the demo data, stop the API and delete `src/SchoolErp.Api/schoolerp.db`.

> **Windows path length.** Keep the project in a short folder such as `C:\dev\school-erp`. In a very deep folder (total path over 260 characters) Windows cannot start the Angular build tools or the .NET test host.

### Demo sign-ins

All demo accounts share one password: the `Seed:Password` value in `src/SchoolErp.Api/appsettings.Development.json`.

| Who | Email | What to try |
|---|---|---|
| School admin | `admin@greenfield.test` | Everything, both campuses |
| Principal | `principal@greenfield.test` | Approve and publish results, announcements |
| Teacher (English, class teacher 5A) | `asha.verma@greenfield.test` | Attendance, marks for her own subject only |
| Accountant | `accountant@greenfield.test` | Invoices, payments, ledger, payroll |
| Parent | `neha.sharma@example.com` | Use the **Parent** tab: a one-time code is shown on screen in development |
| Second tenant | `admin@oakridge.test` | Sees none of Greenfield's data |
| Platform operator | `platform@schoolerp.test` | Tenant directory, support access requests |

Switch the campus to **Greenfield Dubai** and print a report card to see the Arabic, right-to-left PDF. Switch the interface language (top bar) to Arabic to mirror the whole UI.

The public admissions form is at http://localhost:4200/apply/greenfield.

## Data assistant (chat)

Staff see an **Ask the assistant** button in the bottom corner of every screen. They ask questions in plain English ("Who was absent today?", "Which students in Grade 5 A have overdue fees?") and Claude answers from the school's records.

Claude never touches the database. It asks for data through ten read-only tools in `src/SchoolErp.Api/Modules/Assistant/AssistantTools.cs`, and those tools run as the signed-in user: the same school, campus, class and permission limits apply as on the normal screens. A teacher's assistant cannot see fees; nobody's assistant can see medical notes unless their role can. Every question is recorded in the audit log.

**Turn it on** by giving the API an Anthropic API key, then restarting it:

```bash
dotnet user-secrets set "Anthropic:ApiKey" "<your key>" --project src/SchoolErp.Api
```

(or set the `ANTHROPIC_API_KEY` environment variable). The model is set in `appsettings.json` under `Assistant:Model` (default `claude-opus-5-5`). Each user is limited to 10 questions a minute (`RateLimits:AssistantPerMinute`).

**Before using it with real pupils' data:** each question sends the question and the matching records (names, attendance, marks, balances) to Anthropic's API. Check this against your data-protection obligations (GDPR, DPDP, regional data residency) and Anthropic's data-retention terms, and tell schools in their privacy notice.

## Tests

```bash
dotnet test
```

48 tests: the grading engine (CBSE, IB, Cambridge, GPA, rounding, absences), fee arithmetic, timetable clash detection, field encryption, TOTP against the RFC 6238 vector, the year-end rollover, and a cross-tenant leak test that inserts a row for two tenants into every tenant-owned table and checks neither can read the other's.

An end-to-end API check (113 assertions over the main journeys, permissions and tenant isolation) runs against a freshly seeded server:

```bash
python tests/smoke/smoke.py src/SchoolErp.Api/appsettings.Development.json
```

It ends with a rollover, so delete `schoolerp.db` afterwards.

## How it is put together

```
src/SchoolErp.Api
  Program.cs            startup, auth, rate limits, tenant resolution
  Data/AppDbContext.cs  tenant query filter, soft delete, audit trail, UTC timestamps
  Shared/               permissions, field encryption, TOTP, CSV, PDF, file store
  Modules/
    Identity  Admin  Admissions  Students  Academics  Attendance
    Exams  Fees  Communication  Security  Rollover  Portal
  Seed/DemoSeeder.cs    demo tenants
web/src/app
  core.ts  i18n.ts  crud-table.ts  app.ts  app.routes.ts  pages/
tests/SchoolErp.Tests   unit and database tests
tests/smoke             API smoke test
```

- **Tenancy.** Every tenant-owned entity derives from `TenantEntity`. `AppDbContext` applies one global filter (`TenantId == current tenant && !IsDeleted`) to all of them and sets `TenantId` from the signed-in user on insert, never from the request body. A test fails the build if a new table is added without the filter.
- **Permissions.** A role is a list of permissions plus a scope (school group, campus, assigned sections, own children, self). The six built-in roles are editable templates. The API enforces them; the UI only hides what you cannot use.
- **Configuration over code.** Grading schemes are JSON (bands, pass mark, rounding, absence rule) evaluated by one engine. CBSE, IB, Cambridge and GPA are presets of the same shape. Editing a scheme that results depend on creates a new version.
- **Fees.** Invoices, payments, late fines, refunds and credit notes each post a balanced transaction to a double-entry ledger. Payment gateways implement `IPaymentProvider` (create order, verify signed webhook, refund). Card data never reaches the server.
- **Notifications.** Messages are written to an outbox table and delivered by a background worker through `INotificationChannel` implementations.
- **Audit.** `SaveChanges` records every create, update and delete with before/after values (sensitive fields masked). Exports, imports, sign-ins, erasures and support access are recorded explicitly. No endpoint edits or deletes audit rows.

## Requirement coverage

| ID | Requirement | Status |
|---|---|---|
| FR-01-01 | Tenants and campuses with country, currency, time zone, language, curriculum | Built |
| FR-01-02 | Academic years, terms, grades, sections, subjects | Built |
| FR-01-03 | Roles as permission + scope | Built. Grade and section scopes are covered by one "assigned sections" scope |
| FR-01-04 | Bulk import with validation and dry run | CSV only. Excel files must be saved as CSV |
| FR-01-05 | Custom fields | Partial: definitions for all three record types; shown on the student form only |
| FR-02-01 | Enquiries from a public form and walk-ins | Built |
| FR-02-02 | Configurable application form with document upload | Built |
| FR-02-03 | Pipeline stages | Built |
| FR-02-04 | Schedule tests and interviews, notify applicants | Built (notifications go to the log, see below) |
| FR-02-05 | Enrolment creates student, guardian login, fee account | Built |
| FR-03-01 | Student profile, guardians, siblings, medical notes, documents | Built. Photo upload has an API but no button yet |
| FR-03-02 | Enrolment history | Built |
| FR-03-03 | Transfer, withdrawal, transfer certificate | Built |
| FR-03-04 | Medical and special-needs fields restricted | Built, and encrypted at rest |
| FR-04-01 | Class, subject and teacher mapping | Built |
| FR-04-02 | Period structure and weekly grid | Built |
| FR-04-03 | Teacher and room clash detection | Built |
| FR-04-04 | Substitutions | API only, no screen |
| FR-05-01 | Daily or period-wise attendance | Built |
| FR-05-02 | Configurable absence codes | Built |
| FR-05-03 | Absence alerts within 15 minutes | Built: queued on save, dispatched within seconds |
| FR-05-04 | Staff attendance and leave | Leave requests and approval built. Staff attendance is not |
| FR-05-05 | Offline attendance on mobile | Partial: a register saved while the page is open and offline is queued and synced. No installable app or service worker |
| FR-06-01 | Assessment plans with components and weights | Built |
| FR-06-02 | Configurable, versioned grading schemes | Built |
| FR-06-03 | Marks entry with deadlines, locking, approval | Built |
| FR-06-04 | Report card PDF, RTL | Built with one layout driven by the scheme. Separate templates per curriculum are not built |
| FR-06-05 | Publish results to the parent portal | Built |
| FR-07-01 | Fee heads, structures, installments | Built |
| FR-07-02 | Discounts, scholarships, sibling concessions | Built |
| FR-07-03 | Invoices and online payment through a pluggable gateway | Built against a mock gateway. No Stripe, Razorpay or GCC gateway adapter yet |
| FR-07-04 | Receipts, refunds, credit notes, ledger | Built |
| FR-07-05 | Reminders, late fines, aging | Built. Fines and reminders run from a button, not on a schedule |
| FR-07-06 | Export to accounting systems | Journal CSV only |
| FR-08-01 | Parent and student portal and mobile app | Web portal built. No mobile app |
| FR-08-02 | Announcements by campus, grade or section | Built |
| FR-08-03 | Teacher-parent messaging | Built |
| FR-08-04 | Digital consent with a stored record | Built |
| FR-09-01 | MFA for staff, SSO | TOTP two-step sign-in is available per user but not enforced. No SSO |
| FR-09-02 | Audit log | Built |
| FR-09-03 | Per-student export and deletion | Built |
| FR-09-04 | Support access with tenant approval, time-limited, logged | Built, read-only, revocable |
| FR-YE-01 | Year-end rollover | Built |
| Phase 3 (14) | Payroll | First version built: salary structure per staff member, monthly runs with approval, unpaid-leave deduction, ledger postings, payslip PDFs, bank transfer CSV. No country statutory packs (PF/ESI/TDS, GOSI, WPS) yet: deductions are configured as components |
| Phase 2 (12) | HR / staff records | Partial: staff profiles with teaching load, timetable, leave and payroll. No recruitment or appraisals |

## What differs from the architecture document

- **Database.** SQLite by default so the app runs with no setup. PostgreSQL is wired up (`Database:Provider=postgres`, see `docker-compose.yml`) but has **not been run or tested**. The schema is created with `EnsureCreated`; there are no EF Core migrations yet, and tables have no foreign-key constraints. SQLite stores money as floating point; PostgreSQL would use `numeric`.
- **Second isolation guard.** The plan calls for database row-level security behind the EF filters. Only the EF filters exist.
- **Module boundaries.** Modules are folders in one project and call each other directly. There are no per-module schemas, public contracts or architecture tests.
- **Libraries swapped for small built-in code.** Finbuckle, OpenIddict, Hangfire, SignalR, Redis, Transloco and Angular Material are not used. Tenancy, JWT sign-in, the outbox worker and i18n are implemented directly. Notifications are polled when the bell is opened, not pushed live.
- **Delivery channels.** Email, SMS and push are written to the application log. In-app notifications are real.
- **Translations.** English is complete. Arabic and Hindi cover navigation, sign-in, the dashboard and the whole parent portal; other staff screens fall back to English.
- **Not started.** Docker image, CI pipeline, infrastructure as code, load tests, Playwright end-to-end tests, an accessibility audit.

## Before real use

- Set `Jwt:Key`, `Security:FieldEncryptionKey` and the payment webhook secret from a secret store. The values in `appsettings.Development.json` are for local development only.
- Keep `Payments:Mock:Enabled` off outside development: the mock gateway lets anyone holding a payment link mark it paid.
- Field encryption uses one application key. The plan's per-region key vault and key rotation are not built.
- The grading presets are starting points. Exam boards publish their own boundaries per subject and session.

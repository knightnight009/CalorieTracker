# NutriTrack: AI-assisted calorie & macro tracker

An ASP.NET Core MVC app (.NET 10, EF Core, PostgreSQL, ASP.NET Core Identity) for tracking calories, protein, carbs and fat. It works out your BMR, TDEE and macro targets from your profile, and logs meals from a photo using an AI vision model. You review and edit every AI estimate before anything is saved.

It's built for a few people with separate accounts, running on near-zero-cost hosting: **Render Free** (Docker) + **Neon Free** (PostgreSQL) + **Gemini API free tier** + **Resend free tier** for email.

---

## Contents

1. [Features](#features)
2. [What works without API keys](#what-works-without-api-keys)
3. [Architecture](#architecture)
4. [Local development](#local-development)
5. [Configuration reference](#configuration-reference)
6. [AI provider setup](#ai-provider-setup)
7. [USDA FoodData Central setup](#usda-fooddata-central-setup)
8. [Email setup](#email-setup)
9. [Deploying (GitHub → Render + Neon)](#deploying-github--render--neon)
10. [Database migrations](#database-migrations)
11. [Backup, restore and data deletion](#backup-restore-and-data-deletion)
12. [Free-tier limits](#free-tier-limits-checked-october-2026)
13. [Security notes](#security-notes)
14. [Testing](#testing)
15. [Troubleshooting](#troubleshooting)
16. [Deployment checklist](#deployment-checklist)
17. [Known limitations](#known-limitations)

---

## Features

| Area | What it does |
|---|---|
| **Accounts** | Register, email confirmation, login/logout, forgot/reset password, 2FA, account lockout after 5 failed logins, per-IP rate limit on auth posts, download/delete personal data (ASP.NET Core Identity). |
| **Profile wizard** | Sex used for BMR, date of birth (age is always calculated, never stored), weight & height in kg/lb and cm/ft-in, activity level, workout days/duration/intensity (informational only, never added on top of the activity factor), goal, target weight / body fat, deficit/surplus, protein and fat preferences, time zone. |
| **Nutrition goals** | Mifflin-St Jeor BMR, TDEE = BMR × configurable activity factor, daily and weekly maintenance, % deficit (10/15/20/25) or surplus, or a manual target. Shows the deficit in kcal and %, an estimated weekly change (labelled as an approximation), and warnings for aggressive deficits and targets below BMR. Macro order: protein (g/kg) → fat (% of kcal) → carbs fill the rest. It rejects negative carbs, shows rounding differences, and supports advanced custom grams (must be within 5% of the calorie goal). |
| **Food diary** | Per-day view by meal, previous/next/jump-to-date, per-meal and per-day totals. Add food by search (your foods + USDA), recent foods, saved meals or manual entry; save custom foods; favorites; edit (rescale by servings or edit numbers); delete item or meal; copy a meal to another date; save a meal for reuse. Double submits are ignored (idempotency key). |
| **AI food scanner** | Upload or take a photo (resized in the browser), choose the meal, add context, then see each item separately with portion, grams, kcal, macros, confidence, cooking method and uncertainty notes. Edit, exclude or add rows, then save. The server re-validates everything and recalculates totals. |
| **Dashboard** | Calorie ring, calories/protein/carbs/fat cards with remaining or **over** amounts (never a negative "remaining"), macro bars, a "what's left today" helper, a meal summary with quick-add and quick-scan per meal, and quick actions. |
| **Progress** | Chart.js: daily calories vs goal, protein vs goal, macro distribution, weight trend. Ranges of 7/30/90 days or custom. Weekly averages, goal adherence, weight log with optional body fat %. Unlogged days are gaps, not zeros. |
| **Ops** | Dockerfile (non-root, stateless), `/health` and `/health/ready`, JSON console logs in production, forwarded headers for Render's proxy, data-protection keys stored in Postgres (logins survive restarts), GitHub Actions CI and a manual migration workflow. |

## What works without API keys

| Without… | You still get |
|---|---|
| `AI_API_KEY` / `OLLAMA_BASE_URL` | Everything except photo analysis. The scanner page shows setup instructions and links to manual entry. |
| `USDA_API_KEY` | Search over your own saved foods, manual entry, custom foods, recent foods and saved meals. |
| `EMAIL_*` (local dev) | Confirmation/reset emails are written to the **server console log** so you can click the link. |
| `EMAIL_*` (production) | Only if you set `Auth__RequireConfirmedEmail=false`. Otherwise startup fails with a clear message. Password reset emails are not sent in that mode. |

The only setting you can't skip is `ConnectionStrings__DefaultConnection`.

---

## Architecture

```
src/CalorieTracker.Web
├── Controllers/        Dashboard, Profile, NutritionGoals, FoodDiary, FoodScanner, Progress, Home
├── Models/             Entities (UserProfile, Food, FoodDiaryEntry/Item, WeightLog, SavedMeal/Item,
│                       FoodScan, NutritionGoalSnapshot, ApplicationUser) and enums
├── ViewModels/         Form/view models. Controllers never bind entities directly (no mass assignment)
├── Services/
│   ├── NutritionCalculatorService   Pure, unit-tested: units, age, BMR, TDEE, goals, macros, scaling, totals
│   ├── NutritionPlanService         Loads profile + latest weight → plan; user-local "today"; goal history
│   ├── DashboardService             Day entries, dashboard model, daily totals for analytics
│   ├── FoodNutritionService         USDA FoodData Central client (IFoodNutritionService)
│   ├── Ai/                          IFoodVisionService → Gemini / Ollama providers, prompt, JSON schema,
│   │                                FoodAnalysisParser (validates untrusted output), image validator
│   └── Email/                       Resend (HTTPS API), dev log sender, disabled sender
├── Data/               ApplicationDbContext, EF Core migrations (PostgreSQL)
├── Infrastructure/     Connection-string normalizer, startup config validation, helpers
├── Areas/Identity/     Login page override (adds lockout-on-failure); other pages are the default Identity UI
└── Views/, wwwroot/    Razor + Bootstrap 5 + Chart.js (vendored, no CDN)
tests/CalorieTracker.Tests   xUnit unit tests + WebApplicationFactory integration tests on PostgreSQL
```

**Key decisions**

- **PostgreSQL everywhere** (local Docker, CI service container, Neon in prod). One migration set, no SQLite/Postgres drift.
- **Nutrition snapshots.** Diary and saved-meal items copy the food's name and nutrients when logged, so editing or deleting a food never rewrites history.
- **Diary dates are `date` columns** in the user's local calendar (time zone stored in the profile), so entries never shift across midnight because of UTC conversion.
- **Weight lives only in `WeightLog`.** The profile's "current weight" is today's weight log entry, so there's one source of truth.
- **Goal history.** Every goal change writes a `NutritionGoalSnapshot`, and adherence charts compare each day with the targets that applied *that day*.
- **AI is two-step and untrusted.** The model's JSON is schema-constrained, then validated server-side: implausible items are dropped, calories are cross-checked against macros, text is truncated and confidence clamped. Totals are always recomputed. The result is stored as a `FoodScan` and must be confirmed by the user, and one scan can create at most one diary entry (deterministic idempotency key).
- **No image storage.** Photos are validated (size, extension, declared type, magic bytes), held in memory for the request, sent to the provider and dropped. Nothing touches the disk, so no temp-file cleanup is needed.
- **Ownership** is enforced in every query with the authenticated user id from the auth cookie. Ids in URLs/forms only narrow that set, and a mismatch returns 404.

---

## Local development

**Prerequisites:** .NET 10 SDK, Docker (for PostgreSQL).

```bash
docker compose up -d db
```

```bash
dotnet tool restore
```

```bash
dotnet run --project src/CalorieTracker.Web --launch-profile http
```

Open http://localhost:5149. In Development the app applies migrations on startup and uses the local database from `appsettings.Development.json` (a throwaway container on port 5433).

Confirmation and reset links are printed to the console in Development (look for `DEV EMAIL`).

Secrets for local development go in user secrets, never in `appsettings*.json`:

```bash
dotnet user-secrets --project src/CalorieTracker.Web set AI_API_KEY "your-gemini-key"
```

```bash
dotnet user-secrets --project src/CalorieTracker.Web set USDA_API_KEY "your-usda-key"
```

---

## Configuration reference

All settings are environment variables (ASP.NET Core also reads them from user secrets or appsettings). See [.env.example](.env.example).

| Variable | Required | Purpose |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | **yes** | PostgreSQL. Accepts Npgsql key/value **or** Neon's `postgresql://…?sslmode=require` URL. |
| `ASPNETCORE_ENVIRONMENT` | prod: `Production` | Set in the Dockerfile. |
| `AI_PROVIDER` | no | `gemini` or `ollama`: which configured provider to try first. If it's out of quota or down, the other configured one is tried. |
| `AI_API_KEY` | no | Gemini API key (enables the Gemini provider). |
| `AI_MODEL` | no | Default `gemini-2.5-flash`. |
| `OLLAMA_BASE_URL` / `OLLAMA_MODEL` | no | Local Ollama vision model (default `qwen2.5vl:7b`). |
| `AI_TIMEOUT_SECONDS` | no | Default 60. |
| `AI_SCANS_PER_MINUTE` | no | Per-user rate limit, default 6. |
| `USDA_API_KEY` | no | Enables USDA search. |
| `EMAIL_PROVIDER`, `EMAIL_API_KEY`, `EMAIL_FROM_ADDRESS` | prod* | `resend` + API key + verified sender. *Unless `Auth__RequireConfirmedEmail=false`. |
| `Auth__RequireConfirmedEmail` | no | Default `true`. |
| `Auth__RegistrationEnabled` | no | Default `true`. Set `false` once your accounts exist so strangers can't sign up (and use your AI quota). |
| `AUTH_POSTS_PER_MINUTE` | no | Per-IP limit for login/register/reset posts, default 10. |
| `Database__MigrateOnStartup` | no | `true` only in Development. Keep it `false` in production (see [migrations](#database-migrations)). |
| `Nutrition__ActivityFactors__*`, `Nutrition__KcalPerKgBodyWeight` | no | Tune the activity multipliers / weekly-change estimate. |

At startup the app checks configuration. A missing required value stops it with a message that **names** the setting and never prints its value.

---

## AI provider setup

### Google Gemini (default, hosted, free tier)

As of October 2026, Google's pricing page lists free-tier input/output with image support for `gemini-2.5-flash`, `gemini-2.5-flash-lite`, `gemini-3-flash-preview` and newer flash-lite models. Free-tier rate limits are per project and change often, so check the [pricing](https://ai.google.dev/gemini-api/docs/pricing) and [rate limits](https://ai.google.dev/gemini-api/docs/rate-limits) pages.

1. Create a key at <https://aistudio.google.com/apikey>.
2. Set `AI_PROVIDER=gemini`, `AI_API_KEY=<key>` and optionally `AI_MODEL`.

The app sends the image plus a system prompt, and asks for JSON that matches a schema (`responseMimeType: application/json` + `responseSchema`). The key is sent in the `x-goog-api-key` header from the server only.

> **Privacy:** on the free tier, Google states that submitted content may be used to improve its products. Don't upload photos you wouldn't want processed that way, or use a paid tier / Ollama. User profile data is never sent to the AI.

When the quota runs out (HTTP 429), the user sees a clear message and can add food manually. If Ollama is also configured, it's tried automatically.

### Ollama (free, local)

```bash
ollama pull qwen2.5vl:7b
```

Then set `OLLAMA_BASE_URL=http://localhost:11434` (and `AI_PROVIDER=ollama` to try it first). This suits local use; Render's free instance can't run a vision model.

---

## USDA FoodData Central setup

1. Get a free key at <https://fdc.nal.usda.gov/api-key-signup> (api.data.gov; default limit 1,000 requests/hour per key). `DEMO_KEY` works for quick tests with much lower limits.
2. Set `USDA_API_KEY`.

Search results show nutrients **per 100 g**. When you add one, the server fetches that food again from USDA by its FDC id (it never trusts nutrient values posted by the browser), saves it to *My foods* with source `USDA` and the FDC id, and logs the grams you entered. If USDA is unreachable, search shows a notice and everything else keeps working.

---

## Email setup

Render's free web services **block outbound SMTP ports** (25/465/587), so the app sends email through **Resend's HTTPS API**.

1. Create a [Resend](https://resend.com) account (free plan: 3,000 emails/month, 100/day).
2. Verify a sending domain (or use their test sender while you try it out; it can only send to your own address).
3. Set `EMAIL_PROVIDER=resend`, `EMAIL_API_KEY=re_…`, `EMAIL_FROM_ADDRESS="NutriTrack <no-reply@yourdomain>"`.
4. Register a new account on the deployed app and confirm the email arrives. **Until you've done that, treat email confirmation and password reset as not working.**

---

## Deploying (GitHub → Render + Neon)

### 1. GitHub

```bash
git init
```

```bash
git add . && git commit -m "Initial commit"
```

Create an empty GitHub repository, then push to `main`. CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs on every push and PR: restore → Release build → check migrations are up to date → unit + integration tests (against a Postgres service container) → Docker build. It needs no secrets.

> GitHub Pages can't host this app (it only serves static files). GitHub stores the code and runs CI, and Render runs the server.

### 2. Neon

1. Create a project at <https://neon.tech> (Free plan), in a region close to your Render region.
2. Copy the connection string from **Connect**. Use the **direct** (non-pooler) string for migrations. Either works for the app.
3. Recommended: append `Maximum Pool Size=10` (key/value form) to stay well below the free compute's connection limits.

### 3. Apply the schema (once, and before any deploy that adds a migration)

Choose one:

- **GitHub Actions:** add a repository secret `PRODUCTION_DATABASE_URL` (Settings → Secrets → Actions, ideally in an environment called `production`). Then Actions → **Migrate database** → Run workflow → type `migrate`.
- **From your machine:**

  ```bash
  ConnectionStrings__DefaultConnection="<neon-connection-string>" dotnet ef database update --project src/CalorieTracker.Web
  ```

### 4. Render

1. Render dashboard → **New → Blueprint** → pick the repo. [render.yaml](render.yaml) creates a free Docker web service with `/health` as its health check and **auto-deploy only after the GitHub checks pass** (`autoDeployTrigger: checksPass`).
2. Fill in the secret env vars it asks for: `ConnectionStrings__DefaultConnection`, `AI_API_KEY`, `USDA_API_KEY`, `EMAIL_API_KEY`, `EMAIL_FROM_ADDRESS`.
3. Wait for the deploy, then open `https://<service>.onrender.com/health` (should say `Healthy`) and `/health/ready` (checks the database).

What's automatic and what's manual:

| Step | Automatic? |
|---|---|
| Build + tests on push/PR | ✅ GitHub Actions |
| Deploy to Render | ✅ after checks pass on `main` |
| Database migrations | ❌ manual (workflow or CLI), on purpose: no schema change runs unattended against production |
| Neon/Render/Resend/Gemini accounts and keys | ❌ manual, one time |

### 5. Smoke test

Register two accounts → confirm both emails → log in as A, log a meal → log in as B and check A's meal isn't visible → restart the Render service (Manual Deploy → Restart) and check A's data and login are still there.

### Moving to paid or other hosting later

The app is a plain container plus PostgreSQL: point `ConnectionStrings__DefaultConnection` at any Postgres (Azure Database for PostgreSQL, RDS, Render Postgres paid, Supabase) and deploy the same image to Azure App Service (container), Fly.io, a paid Render instance (no sleep), and so on. To move the data, do a `pg_dump` → `pg_restore` (below).

---

## Database migrations

- Migrations live in `src/CalorieTracker.Web/Data/Migrations` and are PostgreSQL-specific.
- Add one after changing entities:

  ```bash
  dotnet ef migrations add <Name> --project src/CalorieTracker.Web
  ```

- **Production never migrates automatically.** `Database__MigrateOnStartup` is `false` outside Development, and nothing ever calls `EnsureDeleted`/`EnsureCreated` against production.
- Before applying, review the generated migration for anything destructive (`DropColumn`, `DropTable`, type changes). To review the exact SQL, generate an idempotent script:

  ```bash
  dotnet ef migrations script --idempotent --project src/CalorieTracker.Web -o migrate.sql
  ```

- Order for a release with schema changes: **back up → migrate → deploy**. Keep migrations backward compatible (add first, remove in a later release) so the running version keeps working during the deploy.

## Backup, restore and data deletion

**Backups.** Neon Free keeps a short restore window (6 hours of history). For real backups, run a dump regularly (weekly, plus before every migration) and keep it somewhere private, **outside the repository**:

```bash
pg_dump "<neon-direct-connection-string>" --format=custom --no-owner --file=nutritrack-$(date +%F).dump
```

**Restore** (into an empty database or a new Neon branch):

```bash
pg_restore --no-owner --clean --if-exists --dbname="<target-connection-string>" nutritrack-YYYY-MM-DD.dump
```

Use a `pg_dump` version ≥ the server's major version (17).

**User data deletion.** *Profile & Settings → Download or delete my data → Delete* removes the Identity user. Every user-owned table (profile, diary, foods, saved meals, weight logs, goal history, scan records) has a cascade delete from the user, so all of that user's data is gone in one transaction. Users can also delete single entries, foods, weigh-ins and scan records at any time. Food photos are never stored.

---

## Free-tier limits (checked October 2026)

| Service | Free tier | What it means here |
|---|---|---|
| **Render Free web service** | Sleeps after 15 min idle, ~1 min to wake. 750 instance-hours/month per workspace. Ephemeral filesystem. Outbound SMTP blocked. No SLA. | The first request after idle is slow. The app keeps no state on disk (keys in DB, no uploads), so sleep/redeploy loses nothing. Email goes via HTTPS. One always-on service uses ~720 h, inside the allowance. |
| **Neon Free** | 1 GB storage per project, 100 CU-hours/month per project, compute suspends after 5 min idle, 6-hour restore window. | Cold start adds a short delay to the first query. EF retries transient failures (`EnableRetryOnFailure`). Text-only data stays tiny for a few users. |
| **Gemini API free tier** | Free input/output for flash models, rate-limited per project, content may be used to improve Google products. | Fine for a handful of scans a day. On a 429 the app says so and suggests manual entry. |
| **Resend Free** | 3,000 emails/month, 100/day. | Plenty for confirmation and reset emails. |
| **USDA FDC** | Free key, 1,000 requests/hour. | Plenty. |
| **GitHub Actions** | Free minutes for public repos, and a monthly allowance for private ones. | One CI run takes a few minutes. |

**Render's own free Postgres expires after 30 days**, which is why the database is on Neon. If the free sleep is a problem, a paid Render instance or Azure App Service runs the same container without changes.

---

## Security notes

- Identity with framework password hashing. Minimum 10-character passwords. Lockout after 5 failed logins for 15 minutes. Email confirmation. Login errors don't reveal whether an account exists.
- Auth cookie: `HttpOnly`, `Secure` (production), `SameSite=Lax`, 14-day sliding expiry.
- Global "logged in" fallback policy: every page is private unless explicitly `[AllowAnonymous]` (landing, privacy, error, health, Identity login/register pages).
- Antiforgery validation on every MVC POST (`AutoValidateAntiforgeryToken`).
- Rate limits: AI analysis per user, login/register/reset POSTs per IP.
- Uploads: 8 MB limit (Kestrel + form + validator), JPEG/PNG/WebP only, content type must match the extension, magic bytes checked, processed in memory.
- AI output is treated as untrusted: schema-validated, bounded, truncated and HTML-encoded by Razor. User notes are wrapped as data in the prompt to limit prompt injection.
- Secrets only from environment/user secrets. They never reach the browser and are never logged. Provider error bodies are logged trimmed and never include keys (keys go in headers).
- Production: generic error page with a reference id (details in server logs only), HSTS, HTTPS redirect via forwarded headers, `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`.
- Health endpoints return only `Healthy`/`Unhealthy`.

## Testing

```bash
dotnet test
```

- **Unit tests:** calculator (BMR, TDEE, units, age edge cases, goals, guardrails, macros, scaling, totals, remaining/excess, averages), AI output parsing/validation, provider fallback, image validation, USDA parsing, Neon URL conversion, time zones, startup config errors.
- **Integration tests** (real app + PostgreSQL): the database is dropped and **migrated from scratch** on each run. They cover login required on all private pages, health checks, lockout after 5 bad passwords, **two-user isolation** (user A gets 404 on read/edit/delete/copy/add-from of user B's items, foods, entries, weigh-ins and scans, and B's data is unchanged), double-submit idempotency and day totals, snapshot history after editing a food, server-side rescale, the scanner without an AI provider, the profile wizard → targets → dashboard, and error pages not leaking configuration.
- Integration tests use `TEST_DATABASE_CONNECTION` or default to the local compose database on port 5433.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `Configuration is incomplete: ConnectionStrings__DefaultConnection is not set` | Set the variable (in Render: Environment tab). |
| `Email confirmation is required but no email provider is configured` | Configure Resend, or set `Auth__RequireConfirmedEmail=false`. |
| `relation "AspNetUsers" does not exist` | Migrations haven't been applied. See [Database migrations](#database-migrations). |
| First page load takes ~1 minute | Render free instance waking up (plus Neon compute resume). Expected. |
| Everyone was logged out after a deploy | Shouldn't happen: data-protection keys are in the `DataProtectionKeys` table. Check the DB connection. |
| "The AI provider's free quota is used up" | Gemini free-tier limit reached. Wait, configure Ollama as a fallback, or add food manually. |
| "The configured AI model wasn't found" | Check `AI_MODEL` against the current Gemini model list. |
| USDA search shows "couldn't be reached" | Check `USDA_API_KEY`, or wait if the API is down. Manual entry still works. |
| Confirmation email never arrives | Check the Resend dashboard logs and that the sender domain is verified. |
| `has-pending-model-changes` fails in CI | You changed entities without adding a migration. |
| Locked out | Wait 15 minutes, or reset your password. |

## Deployment checklist

1. ☐ **GitHub:** create the repo, push `main`, confirm the CI workflow is green.
2. ☐ **Neon:** create a project (Free) and copy the direct connection string.
3. ☐ **Connection string:** store it as GitHub secret `PRODUCTION_DATABASE_URL` and keep it for Render.
4. ☐ **Render:** New → Blueprint → this repo (free Docker web service from `render.yaml`).
5. ☐ **Environment variables in Render:** `ConnectionStrings__DefaultConnection`, `AI_API_KEY`, `USDA_API_KEY`, `EMAIL_API_KEY`, `EMAIL_FROM_ADDRESS` (others have defaults).
6. ☐ **Migrations:** Actions → Migrate database → `migrate` (or `dotnet ef database update`), then check `/health/ready` returns `Healthy`.
7. ☐ **AI & email:** Gemini key from AI Studio, Resend domain verified, then test-register to confirm the email arrives and a photo scan works.
8. ☐ **Auth & isolation:** create both personal accounts, check each sees only their own data, then set `Auth__RegistrationEnabled=false`.
9. ☐ **Public URL:** log a meal, restart the service, and confirm the data and login persist.
10. ☐ **Quotas understood:** Render sleeps after 15 min, Neon suspends after 5 min, Gemini free tier is rate-limited and may use content for training, Resend 100/day, and you have a weekly `pg_dump` plan.

## Known limitations

- AI estimates come from a photo and can be well off, especially for oils, sauces and portion size. That's why every item is editable and labelled with a confidence score.
- AI recognition and USDA lookup are separate steps. AI items aren't automatically matched to USDA entries yet; you can replace an AI row with a USDA food from search.
- Only one email provider (Resend) is implemented.
- Camera capture uses the browser's native file picker with `capture` (rear camera on phones), not a live in-page camera view.
- The goal page recalculates on save rather than with a live client-side preview.
- Personal-data download covers the Identity account fields; diary data export (CSV) isn't implemented yet.

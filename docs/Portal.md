# The customer portal

Where somebody who bought a stand looks after it themselves: resets a user's
password, restarts the container, sees what their plan includes, lets their
bookkeeper in.

Served by this same application, at `/portal`. **Off by default.**

---

## What it is not

It is not the operator panel with a narrower menu. The panel's API can delete a
database, release a tenant, reassign an image and read every customer's row; a
portal built as a policy on top of it would be one forgotten attribute away from
handing that to a customer.

So the portal is:

| | Operator panel | Customer portal |
|---|---|---|
| Accounts | `OperatorAccount` — one, seeded from config, no registration by design | `CustomerAccount` — registration, e-mail verification, self-service reset |
| Way in | Cloudflare Access, or break-glass on the loopback listener | e-mail + password, optional TOTP |
| Scheme | `CloudflareAccess`, `OperatorSession` | `PortalSession` |
| Policy | default + fallback | `AuthSetup.PortalPolicy` **only** |
| API | `/api/tenants/**` | `/api/portal/**` |
| Bundle | `index.html` | `portal.html` |

Neither credential works on the other's endpoints. That is enforced by which
schemes each authorization policy names, and it is checked by
`PortalAuthPolicyTests` — because it is invisible at every call site and has no
compile-time consequence when it is wrong.

> **The trap that test exists for.** `[Authorize(AuthenticationSchemes = "PortalSession")]`
> looks like it pins the scheme list. It does not. With no policy named,
> `AuthorizationPolicy.CombineAsync` still folds in the default policy, and
> `Combine` takes the **union** of the scheme lists — so the portal's endpoints
> silently also accepted `CloudflareAccess` and `OperatorSession`. Naming a
> policy (`[Authorize(Policy = AuthSetup.PortalPolicy)]`) is what keeps the list
> to one. Nothing about the broken form fails a build or a click-through.

---

## Turning it on

**`Portal:Enabled` is false by default.**

> **Changed 2026-09-23.** The Cloudflare Access bypass below is NO LONGER
> required when the customer cabinet on `getzulo.com` is the front end. The
> cabinet talks to `/api/portal/**` from inside the network, so nothing at the
> edge routes a customer here and the panel's hostname stays closed to everyone
> but staff. The bypass is still needed if you serve the built-in SPA at
> `/portal` directly — see `Portal:VerifyPath` below for the other half of that
> choice.

When the built-in SPA IS the front end, switching this on is a decision about
the edge, not just about this file.

This application sits behind Cloudflare Access. Customers are not staff, so
Access blocks them before the request ever reaches here. Before enabling:

1. **Cloudflare Access must BYPASS `/api/portal/*` and `/portal*`.** Without it
   the portal is on, correct, and unreachable — and the symptom is a Cloudflare
   login page, which reads as a DNS or routing problem.
2. **`Portal:PublicUrl` must be set**, e.g. `https://my.zulo.one`. Every
   verification and reset link is built from it. The application refuses to send
   mail when it is empty, and logs an error at start-up, because a letter whose
   only purpose is a link with no host is worse than no letter.
3. **`Mail:Enabled` must be true and SMTP must work.** Registration goes nowhere
   without it. `PortalMailer` warns loudly when it drops a letter, because the
   endpoints deliberately answer "check your mailbox" either way.

The link is built from configuration and **never** from the request's `Host`
header. A link built from an attacker-supplied host is how account takeover by
host-header injection works: the mail reaches the right mailbox carrying a link
to somebody else's server, and the person who owns the address hands over their
own token.

### Settings

| Key | Default | Notes |
|---|---|---|
| `Portal:Enabled` | `false` | See above. Off means every `/api/portal` endpoint answers 404, and existing sessions stop authenticating. |
| `Portal:PublicUrl` | empty | Where a browser reaches the portal. Mail links are built from it. |
| `Portal:SessionHours` | 12 | |
| `Portal:VerifyTokenHours` | 48 | |
| `Portal:ResetTokenMinutes` | 60 | Short: a password equivalent sitting in a mailbox. |
| `Portal:LockoutThreshold` | 8 | Per account, checked before the password is verified. |
| `Portal:LockoutMinutes` | 15 | |
| `Portal:ClaimByAdminEmail` | `true` | See *How somebody gets a stand*. Turn **off** where stands are provisioned against a shared internal address. |
| `Portal:VerifyPath` | `/portal/verify` | Path the verification link points at, appended to `PublicUrl`. `{locale}` is replaced with the account's language. For the getzulo.com cabinet: `/{locale}/cabinet/verify`. Hardcoded until 2026-09-23, which made every mailed link point at a 404 the moment the front end moved. |
| `Portal:ResetPath` | `/portal/reset` | Same, for password reset. |
| `Portal:Locales` | `en, ru, ar, uk` | Allow-list for `{locale}`. `CustomerAccount.Locale` is supplied at registration, so this is a guard, not a formality: without it a sign-up carrying `../../somewhere` mails itself a link that leaves the cabinet — from our domain, with a live token. |
| `Portal:DefaultLocale` | `en` | Used when the account has no language, and as the fallback for anything not on the list. |

---

## How somebody gets a stand

Two ways, and the first covers the ordinary case with no operator involved.

**Self-claim.** Provisioning already records `Tenant.AdminEmail` — the person the
stand was built for. When an account verifies that same address, it gets a
membership: `Owner` if the stand has no owner yet, `Member` if it already does.
Runs at verification **and at every sign-in**, because customers commonly
register before their stand exists, and a claim that only ran at verification
would leave them staring at an empty portal for ever.

Demo tenants are excluded — they are pooled, on a timer, and read-only in the
portal anyway.

**Operator grant.** `POST /api/tenants/{id}/members` with `{"email": "..."}`.
Needed because self-claim cannot cover a stand provisioned against an internal
address, a typo, or a company whose signatory is not the administrator. Those
have no owner and no way to acquire one — a dead end only an operator can open.
The account must already exist: creating one from the panel would mean minting a
credential for an unproven address.

---

## Who may do what

Three inputs, and **the plan is not one of them.**

1. **Role** — `Owner` or `Member` on that stand.
2. **Subscription state** — `Tenant.Status`.
3. **Demo** — pooled infrastructure on a clock.

| | Owner | Member |
|---|---|---|
| See the stand, its stats, its log | ✓ | ✓ |
| Reset a stand user's password | ✓ | ✓ |
| Restart | ✓ | ✓ |
| Stop / start | ✓ | — |
| Add and remove members | ✓ | — |

Reads are never refused, deliberately — including on a suspended stand, so that
the page explaining *why* it is suspended is reachable.

### The licence check

**A `Suspended` stand is read-only.** That is the whole payment enforcement, and
it runs off state an operator already sets when a subscription is not settled —
no separate billing flag to fall out of step with reality. Same for a stand whose
`ExpiresAt` has passed.

`Tenant.StoppedByCustomer` exists because of this. `Suspended` is reached two
ways — a customer pressing stop, and an operator suspending for non-payment —
and without telling them apart, the portal's **Start** button would be a
one-click way out of suspension for non-payment. Start refuses unless the stand
was stopped from the portal.

### Why the plan gates nothing

The published price list differentiates on seats, backup retention, support
response, a sandbox stand and an availability target. Not one of those is "may
restart the container". Gating restart behind Business would be a restriction we
never sold, invented in code — the same drift as promising an entitlement we
never sold, only harder to notice, because it only ever makes a customer's day
worse.

So `PlanCatalog` has two halves that do not touch:

- **`Describe(code)`** — the published figures, for display. Mirrors
  `getzulo.com/src/messages/*.json` under `pricing.tiers`. **If the price list
  changes and this does not, the portal tells customers something the site
  contradicts** — they change together.
- **`Decide(capability, tenant, role, now)`** — the access gate above.

An unrecognised or missing plan code returns a record with **no figures**, rather
than falling back to Start. Falling back would tell an Enterprise customer whose
code was mistyped that they have a 14-day restore window, and make a typo
indistinguishable from a deliberate choice.

---

## What a customer is never shown

`PortalController.Card` is the only shape that leaves the portal. It deliberately
omits everything on the operator's `Summary`: `DatabaseName`, `DatabaseRole`,
`DatabasePassword`, `LogPassword`, `JwtSigningKey`, `ImageTag`, `ContainerId`,
`LastError`.

`LastError` is the easy one to get wrong. It reads as harmless status and carries
operator-facing detail — a connection string from a failed provision, a Docker
message naming a host. Lifecycle failures answer "That did not work. We have been
told about it" and put the exception in the log.

---

## Every refusal says why

`PortalDecision` carries a sentence, and the UI shows it verbatim. A bare 403 on
"restart my own stand" reads as a bug and generates the support ticket this
portal exists to prevent. "This stand is suspended, so it is read-only here.
Settling the subscription restores it." is self-service.

Note that `Forbid()` is not usable for this: it asks the authentication scheme to
write the challenge, and a bearer scheme produces an empty body — so the sentence
would never arrive. `PortalController.Forbid403` returns the status with the
reason in it.

---

## Enumeration

Register and forgot-password always answer the same way, whatever happened —
including for a malformed address, where a `400` would be as good an oracle as a
`404`. Registering an address that already has a verified account sends a
**password reset** to it and does not touch the password: that helps the real
owner and tells an impostor nothing.

Asking for a stand the caller is not a member of answers **404, not 403**. A 403
would confirm the id exists, which turns the endpoint into a way to enumerate the
fleet one guid at a time.

The one deliberate exception: signing in with a correct password to an unverified
account says so. Only *after* the password is proved — by then the person has
told us nothing they did not know — and it is the only way they can find out why
a correct password is not working. It does **not** send anything; resending is a
button, on `POST /api/portal/auth/resend`.

---

## Two things driving it live changed

Both found running the thing against a real server and a real SMTP catcher, and
neither was visible in a unit test.

**Reissuing a verification token used to kill the previous one.** Purging
outstanding tokens of the same kind looked tidy and broke the ordinary path:
register → letter arrives → try to sign in before clicking it → the sign-in
reissues → the link in the mail already open is now dead, and says "expired or
already used". The probe produced **three identical letters of which only the
last worked**.

Now only `ResetPassword` purges. Nothing is bought by purging verification
tokens: every one lands in the same mailbox, so a second live link widens nothing
that compromising the mailbox does not already own, and each is single-use with
its own expiry. A reset token is different in kind — a password equivalent — and
there the shorter window earns its cost.

**A failed sign-in used to mail the link.** Which meant anybody who knew a
customer's address could fill that person's mailbox by attempting to sign in.
Sending is now its own endpoint, behind a button, and only for an account that is
actually unverified.

---

## Reading the code

| File | What lives there |
|---|---|
| `Portal/CustomerAccount.cs` | Account, session and one-shot token entities |
| `Portal/TenantMembership.cs` | Account × tenant × role |
| `Portal/PlanCatalog.cs` | The price list, and the access gate |
| `Portal/PortalSessionHandler.cs` | The scheme, and `PortalTokens` |
| `Portal/PortalAuthController.cs` | Register, verify, sign in, forgot, reset |
| `Portal/PortalController.cs` | The stands — every one reached via `ResolveAsync` |
| `Portal/PortalAdminController.cs` | The operator's grant and revoke |
| `Portal/TenantClaimService.cs` | Self-claim by verified address |
| `Portal/PortalMailer.cs` | The two letters |
| `web/src/portal/**`, `web/portal.html` | The UI, a second Vite entry |

`ResolveAsync` is the only way `PortalController` obtains a tenant, and it
reaches it by **joining from the caller's membership** rather than looking up an
id and checking afterwards. A forgotten check is invisible; a forgotten join does
not compile into anything that returns a tenant.

The UI is a **separate bundle**, not a route inside the dashboard. The
dashboard's code is the fleet — every screen naming tenant slugs, node names,
image tags. Shipping that to a customer's browser and relying on the router never
to render it is not a boundary. (It shows in the build: 17 kB against 234 kB.)

---

## Not built

- **Self-service backup restore.** Retention genuinely differs by plan (14 vs 90
  days), so this is the one action a plan gate would legitimately cover.
- **Invitations that create an account.** Today the person must sign up first.
- **Billing.** The portal reads `Plan` and shows what it includes; nothing here
  charges anybody or changes a plan.
- **Sandbox stands**, which Business and Enterprise include on the price list.
- **TOTP enrolment in the UI.** The backend verifies a second factor when
  `TotpSecret` is set, but nothing in the portal sets it yet.

# Deploying the free-tier demo

Optional. The graded deliverable is `docker compose up --build`, which works standalone. This adds a
public URL a reviewer can click.

**No credit card is required for any step.** Render's free tier needs no payment details, unlike
Google Cloud Run which requires a billing account even for free usage.

| Piece | Service | Cost |
|---|---|---|
| API container | Render (free web service) | free, no card |
| Frontend | Cloudflare Pages | free, no card |
| MongoDB | Atlas M0 | free |
| Redis | Upstash | free |
| RabbitMQ | CloudAMQP Little Lemur | free |

Because every connection is an environment variable, **the image does not change** between local
Compose and the cloud. Deployment is configuration, not a code branch.

---

## 1. Push to GitHub

```bash
gh repo create inventory-hold-service --private --source=. --push
```

## 2. API on Render

1. [dashboard.render.com](https://dashboard.render.com) → **New → Blueprint** → pick the repo.
   Render reads [`render.yaml`](render.yaml) and creates the service.
2. It prompts for the four values marked `sync: false`. Paste from your local `.env`:
   - `Mongo__ConnectionString`
   - `Redis__ConnectionString`
   - `RabbitMq__Uri`
   - `Cors__AllowedOrigins__0` — leave a placeholder for now; step 4 fills it in.
3. Deploy. The URL looks like `https://inventory-hold-api.onrender.com`.
4. Confirm: `curl https://inventory-hold-api.onrender.com/health` reports all three Healthy.

**Atlas network access:** add `0.0.0.0/0` under Network Access, or Render cannot connect. Render
free services have no static outbound IP to allowlist.

## 3. Frontend on Cloudflare Pages

1. [dash.cloudflare.com](https://dash.cloudflare.com) → **Workers & Pages → Create → Pages** →
   connect the repo.
2. Build settings:
   - Root directory: `web`
   - Build command: `npm run build`
   - Output directory: `dist`
3. Environment variable: `VITE_API_BASE_URL` = the Render URL from step 2.
   Vite inlines this **at build time**, so changing it later needs a rebuild.
4. Deploy. The URL looks like `https://inventory-hold.pages.dev`.

## 4. Close the CORS loop

Back in Render, set `Cors__AllowedOrigins__0` to the exact Pages origin
(`https://inventory-hold.pages.dev`, no trailing slash) and redeploy. The browser blocks the API
otherwise.

---

## What a reviewer sees

With `Hold__ExpirationMinutes=1` on the demo, the whole system is demonstrable in one sitting:

1. Place a hold → inventory drops immediately
2. The countdown ticks down
3. **Wait a minute** → the hold expires on its own and the stock comes back, with nobody touching
   anything

That single interaction exercises atomic deduction, the expiry sweeper, event publishing, and cache
invalidation together.

Add this to the README next to the link:

> ⚠️ Free-tier demo. The API sleeps when idle, so the first request may take ~30–50 seconds to wake.
> Hold expiry is set to 1 minute here so expiration is observable; the default is 15 minutes.

## Known free-tier limits

- **Render free services spin down after inactivity.** The first request after a sleep is slow. This
  is the main reason the local Compose stack remains the primary way to evaluate the project.
- **Atlas M0 is capped at 100 operations/second** and pauses after 30 days idle. Fine for a demo;
  the concurrency test in the README was run locally where no such cap applies.
- **CloudAMQP Little Lemur** is a shared broker with queue and message caps.

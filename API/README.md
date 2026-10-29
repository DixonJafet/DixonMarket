# MarketDixon API

ASP.NET Core 8 API for product browsing, signup/login/logout, persisted mock checkouts, and personalized recommendation offers.

## Run locally

From this directory:

```powershell
dotnet restore
dotnet run
```

In Development, the API reads `SecondConnectionString` from the repository-root `variables.txt` if `ConnectionStrings:MarketDixonDb` or `MARKETDIXON_DB_CONNECTION` is not set. It creates the required user, token, checkout, profile, offer, and model tables on startup. `DB/Phase4.sql` contains the Phase 4 recommendation schema. Do not publish `variables.txt`.

For a stable local signing key, set `MARKETDIXON_JWT_SIGNING_KEY` to a private random value of at least 32 UTF-8 bytes. In non-Development environments, this setting is required.

## Endpoints

| Method | Route | Description |
| --- | --- | --- |
| GET | `/api/health` | Liveness check |
| POST | `/api/auth/signup` | Register a username and password; returns a bearer token |
| POST | `/api/auth/login` | Verify credentials and return a bearer token |
| POST | `/api/auth/logout` | Revoke the current bearer token |
| GET | `/api/auth/me` | Return the authenticated account |
| GET | `/api/products?page=1&pageSize=20` | Browse products; supports `search`, `area`, `category`, and `subcategory` filters |
| GET | `/api/products/{barcode}` | Get one product |
| GET | `/api/categories` | Return the Area → Category → Subcategory hierarchy |
| POST | `/api/payment/checkout` | Authenticated mock payment; persists the checkout and updates the user's profile |
| GET | `/api/recommendations/offers` | Authenticated discovery and favorite offers with server-calculated prices and discounts |
| GET | `/api/recommendations/profile` | Authenticated profile summary based on up to the last 30 checkouts |

Product pages default to 20 items and allow 1-100 items per page. Prices are in Costa Rican colones. Passwords are stored as PBKDF2-SHA256 salted hashes, never in plain text. Access tokens are signed HS256 JWTs and expire after two hours. Logout requires the current bearer token and revokes it immediately; revoked token hashes are stored until token expiry.

## Phase 4 recommendations

Checkout accepts a unique `idempotencyKey` and product `items`; each item has a `barcode`, `quantity`, and optional `offerId`. The API takes product prices from SQL, verifies that any offer belongs to the signed-in user and is still active, and returns the same purchase result when an identical idempotency key and cart are retried. Reusing the key with a different cart returns `409 Conflict`.

Example checkout body:

```json
{
  "idempotencyKey": "e36c7622-7afb-4b3c-a761-a6b2ddfc97dd",
  "items": [
    { "barcode": "7441001234567", "quantity": 2, "offerId": null }
  ]
}
```

After checkout, the API incrementally updates the user's product, category, subcategory, and brand affinities, evicting the contribution from the checkout that falls outside the latest 30 by purchase sequence. Users with fewer than 30 checkouts use all their available history; new users can receive discovery offers without any purchase history. The next offers request generates and stores a fresh snapshot with discovery offers and repeat-purchase favorites separated. Each offer stores its model version, discount (5%-20%), and final CRC price so the displayed price is the one used at checkout. The shared model provides popularity and co-purchase rankings when trained; until then, the API ranks fallback products by observed store-wide checkout frequency, then uses in-stock catalog products if there is no store-wide history.

To run the logic tests:

```powershell
dotnet test ..\API.Tests\MarketDixon.Api.Tests.csproj --configuration Release
```

The shared model is recalculated by the one-shot `RecommenderJob`; it reads purchases from SQL and stores model associations and popularity in SQL:

```powershell
$env:ConnectionStrings__MarketDixonDb = "<connection string>"
dotnet run --project ..\RecommenderJob\MarketDixon.RecommenderJob.csproj --configuration Release
```

Run the job manually or schedule it as an Azure Container Apps Job. Configure the database connection as a secret/environment variable in the hosting platform; do not commit it or pass it in source control.

### Current Azure deployment

The training image and manual Container Apps Job are deployed in `westus3`:

- Resource group: `rg-marketdixon-recommender`
- Container Apps environment: `cae-mdx-recommender` (Consumption only)
- Job: `job-marketdixon-recommender`
- Registry: `mdxrec340f351d.azurecr.io` (Basic)
- Image: `mdxrec340f351d.azurecr.io/recommender-job:20261027`
- Registry pulls use the `id-mdx-recommender` managed identity with `AcrPull`; the SQL connection is stored as a Container Apps secret.

The job is manual-only, has no schedule, uses one 0.25-vCPU / 0.5-GiB replica, and does not retry failed executions. Start one training run when desired:

```powershell
az containerapp job start --name job-marketdixon-recommender --resource-group rg-marketdixon-recommender
```

Inspect executions and their logs:

```powershell
az containerapp job execution list --name job-marketdixon-recommender --resource-group rg-marketdixon-recommender
az containerapp job logs show --name job-marketdixon-recommender --resource-group rg-marketdixon-recommender --execution <execution-name> --replica <replica-name> --container job-marketdixon-recommender
```

The verified initial run trained model version `5a86bd8df40f491f8e20d2cb77abab65` from 45 purchases, producing 92 popular products and 270 directed associations. Manual jobs incur compute charges only while executing; the Basic registry remains provisioned and is approximately USD 0.1666/day (about USD 5.07 per 30.4-day month), plus image storage. Consumption grants are shared across the subscription, and Log Analytics charges can apply if included ingestion allowances are exceeded. There is intentionally no recurring training schedule.

To create the three demo clients and submit 15 idempotent checkouts for each, run the script only against an API deployment that includes Phase 4:

```powershell
.\..\TempFiles\SeedRecommendationClients.ps1
```

The script prompts for a shared demo password and never stores it in the project. It logs in rather than creating a duplicate account if the username already exists. Check `GET /api/recommendations/profile` and `GET /api/recommendations/offers` after seeding to verify the resulting profiles and offers.

## Configuration for hosting

Set these application settings in the hosting provider's secure configuration panel:

- `ConnectionStrings__MarketDixonDb`: SQL Server connection string with access to the product schema and permission to create the required tables and indexes.
- `MARKETDIXON_JWT_SIGNING_KEY`: private signing key with at least 32 UTF-8 bytes.
- `ASPNETCORE_ENVIRONMENT`: `Production`.
- `MARKETDIXON_CORS_ORIGINS`: optional semicolon-separated list of trusted website origins. Local Vite origins `http://localhost:5173` and `http://127.0.0.1:5173` are allowed by default.

The publish-settings file contains deployment credentials and must never be committed, printed, or copied into the published output. The API intentionally does not read `variables.txt` outside Development.

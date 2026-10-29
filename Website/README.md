# MarketDixon storefront

React storefront for browsing the MarketDixon catalog, filtering by area/category, maintaining a local shopping basket, signing in/up, and submitting the mock checkout.
The interface is available in English and Spanish; the selected language is saved in browser storage. Product names and catalog-provided category names remain as provided by the API.

## Run locally

```powershell
npm install
npm run dev
```

Open `http://localhost:5173`. The app uses the deployed HTTPS API by default. To use another API, set `VITE_API_URL` to its `/api` base URL before starting Vite, for example `http://localhost:5000/api`.

The API allows the local Vite origins by default for development. For another frontend host, set `MARKETDIXON_CORS_ORIGINS` on the API to a semicolon-separated list of exact origins (scheme and host, no path), then restart the API.

The basket is saved in browser local storage. Checkout is a mock and does not charge a payment method or save an order.

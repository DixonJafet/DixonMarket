# 🛒 MarketDixon

**MarketDixon** is a digital grocery storefront inspired by Costa Rican neighborhood markets. It brings everyday products, a bilingual shopping experience, and personalized promotions together in one web application.

---

## 🧩 Overview

MarketDixon combines a React storefront with a .NET API and SQL Server database. Customers can explore and filter a barcode-based product catalog, manage a shopping basket, create an account, and complete a simulated checkout.

The project also explores personalized shopping: purchase history informs product recommendations and individual offers, while a shared recommendation model learns from checkout patterns across the store.

---

## 💻 Tech Stack

| Layer | Technology |
|-------|------------|
| **Frontend** | ⚛️ React 18 |
| **Frontend tooling** | ⚡ Vite |
| **Backend** | 🧱 C# / ASP.NET Core 8 API |
| **Database** | 🗄️ Microsoft SQL Server |
| **Recommendation job** | 🧠 C# / .NET 8 |
| **Infrastructure** | ☁️ Azure Container Apps & Azure Container Registry |
| **Frontend hosting** | ▲ Vercel |

---

## ☁️ Cloud Infrastructure (Azure & Vercel)

| Service | Purpose |
|---------|---------|
| **▲ Vercel** | Hosts the React storefront |
| **☁️ Azure Container Apps Job** | Runs the shared recommendation-model calculation as a batch job |
| **📦 Azure Container Registry** | Stores the recommendation job's container image |
| **📊 Azure Log Analytics** | Collects logs from the recommendation job |
| **🗄️ SQL Server** | Stores the product catalog, accounts, checkouts, recommendation profiles, offers, and model data |

The storefront communicates with the ASP.NET Core API, which owns catalog access, authentication, checkout processing, and offer pricing. Recommendation-model recalculation is separate from customer requests and runs as an on-demand job.

---

## ✨ Features

- 🛍️ Browse a grocery catalog with product details, availability, and CRC pricing
- 🔎 Search products and filter by store area, category, and subcategory
- 🧺 Maintain a shopping basket in the browser
- 🌎 Shop in English or Spanish
- 👤 Register, sign in, and access account-specific features
- 🧾 Submit mock checkouts, recorded with their purchased items
- 🎁 Discover personalized product recommendations and discounts on repeat favorites
- 📱 Explore a responsive storefront designed for everyday shopping

---

## 🧠 Personalized Recommendations

Each checkout updates the customer's recommendation profile using a rolling window of up to the **30 most recent purchases**. The profile summarizes preferences for products, categories, subcategories, and brands.

A shared model, recalculated separately in the Azure job, captures product popularity and products commonly purchased together. The API combines those store-wide patterns with each customer's recent preferences to select distinct offers:

- **✨ Discovery offers** introduce products outside the customer's repeat-purchase history.
- **💛 Favorite offers** highlight products the customer buys repeatedly.
- **🏷️ Personalized discounts** range from **5% to 20%** and are calculated by the API.

Offer prices and model versions are stored so the price shown by the storefront remains consistent when an eligible offer is used at checkout. New customers can receive popular-product recommendations before building a purchase history.

---

## 🔒 Security & Checkout

- Passwords are stored as salted **PBKDF2-SHA256** hashes.
- Authenticated API requests use signed **JWT bearer tokens**; logout revokes the active token.
- Checkout requests use idempotency keys to prevent duplicate purchases when the same request is retried.
- Product and offer prices are validated by the API rather than trusted from the storefront.
- Payments are **simulated**; the project does not process real payment details or charge a payment method.

---

🧠 *Developed by Jafet Dixon*

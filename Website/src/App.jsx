import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { translate, translateError } from "./i18n.js";

const API_URL = (import.meta.env.VITE_API_URL || "https://marcketdixonapi.runasp.net/api").replace(/\/$/, "");
const TOKEN_KEY = "marketdixon.accessToken";
const CART_KEY = "marketdixon.cart";
const LANGUAGE_KEY = "marketdixon.language";
const money = new Intl.NumberFormat("es-CR", { style: "currency", currency: "CRC", maximumFractionDigits: 0 });

function readStoredJson(key, fallback) {
  try {
    const value = localStorage.getItem(key);
    return value ? JSON.parse(value) : fallback;
  } catch {
    return fallback;
  }
}

async function request(path, options = {}) {
  const token = localStorage.getItem(TOKEN_KEY);
  const response = await fetch(`${API_URL}${path}`, {
    ...options,
    headers: {
      ...(options.body ? { "Content-Type": "application/json" } : {}),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...options.headers,
    },
  });
  const contentType = response.headers.get("content-type") || "";
  const data = contentType.includes("application/json") ? await response.json() : null;
  if (!response.ok) {
    const detail = data?.detail || data?.title || data?.error || "Something went wrong. Please try again.";
    throw new Error(detail);
  }
  return data;
}

function Icon({ name, size = 20 }) {
  const paths = {
    search: <><circle cx="11" cy="11" r="7" /><path d="m20 20-4-4" /></>,
    cart: <><path d="M3 4h2l2.2 11.2a2 2 0 0 0 2 1.6h7.7a2 2 0 0 0 2-1.6L20 9H6" /><circle cx="10" cy="21" r="1" /><circle cx="18" cy="21" r="1" /></>,
    user: <><circle cx="12" cy="8" r="4" /><path d="M5 21a7 7 0 0 1 14 0" /></>,
    arrow: <><path d="M5 12h14" /><path d="m13 6 6 6-6 6" /></>,
    plus: <><path d="M12 5v14M5 12h14" /></>,
    close: <><path d="m18 6-12 12M6 6l12 12" /></>,
    leaf: <><path d="M20 4c-8 0-14 3-14 10a6 6 0 0 0 6 6c7 0 8-8 8-16Z" /><path d="M4 21c2-5 6-8 11-11" /></>,
    spark: <><path d="m12 3 1.9 5.8L20 11l-6.1 2.2L12 19l-2-5.8L4 11l6-2.2L12 3Z" /><path d="m19 14 1.1 2.4L22 17l-1.9.8L19 20l-.8-2.2L16 17l2.2-.6L19 14Z" /></>,
  };
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[name]}</svg>;
}

function OfferSection({ title, offers, onAdd, money, translate }) {
  if (!offers.length) return null;

  return (
    <section className="offer-section" aria-label={title}>
      <div className="offer-heading"><span className="eyebrow">{title}</span></div>
      <div className="product-grid">
        {offers.map(({ offerId, product, discountPercent, finalPriceCRC }) => (
          <article className="product-card offer-card" key={offerId}>
            <div className="product-image-wrap">
              <span className="product-chip offer-discount">-{discountPercent}%</span>
              {product.imageUrl ? <img className="product-image" src={product.imageUrl} alt={product.productName} loading="lazy" onError={(event) => { event.currentTarget.style.display = "none"; event.currentTarget.nextElementSibling.style.display = "grid"; }} /> : null}
              <div className="image-placeholder" style={{ display: product.imageUrl ? "none" : "grid" }}><Icon name="leaf" size={34} /><span>{translate("product.placeholder")}</span></div>
              <button className="quick-add" onClick={() => onAdd({ ...product, offerId, discountPercent, priceCRC: finalPriceCRC })} aria-label={translate("product.addToBasket", { name: product.productName })} title={translate("product.addTitle")}><Icon name="plus" size={20} /></button>
            </div>
            <div className="product-info">
              <span className="product-category">{product.category || product.area || translate("product.marketFavorite")}</span>
              <h3>{product.productName}</h3>
              <p>{[product.brand, product.packageQuantity].filter(Boolean).join(" · ") || translate("product.everydayGoodness")}</p>
              <div className="offer-price-row"><strong>{money.format(finalPriceCRC)}</strong><del>{money.format(product.priceCRC)}</del></div>
            </div>
          </article>
        ))}
      </div>
    </section>
  );
}

function App() {
  const [products, setProducts] = useState([]);
  const [categories, setCategories] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [searchInput, setSearchInput] = useState("");
  const [search, setSearch] = useState("");
  const searchRef = useRef(null);
  const [area, setArea] = useState("");
  const [category, setCategory] = useState("");
  const [subcategory, setSubcategory] = useState("");
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [page, setPage] = useState(1);
  const [totalItems, setTotalItems] = useState(0);
  const [cart, setCart] = useState(() => readStoredJson(CART_KEY, []));
  const [cartOpen, setCartOpen] = useState(false);
  const [authOpen, setAuthOpen] = useState(false);
  const [authMode, setAuthMode] = useState("login");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [user, setUser] = useState(null);
  const [offers, setOffers] = useState([]);
  const [language, setLanguage] = useState(() => localStorage.getItem(LANGUAGE_KEY) === "es" ? "es" : "en");
  const languageRef = useRef(language);
  const [notice, setNotice] = useState("");
  const [submittingAuth, setSubmittingAuth] = useState(false);
  const [checkingOut, setCheckingOut] = useState(false);
  const checkoutIdempotencyKey = useRef(null);
  const pageSize = 24;

  const areaOptions = useMemo(() => [...new Set(categories.map((item) => item.area))].sort(), [categories]);
  const categoryOptions = useMemo(() => {
    const current = categories.find((item) => item.area === area);
    return current?.categories.map((item) => item.name) || [];
  }, [area, categories]);
  const subcategoryOptions = useMemo(() => {
    const currentArea = categories.find((item) => item.area === area);
    const currentCategory = currentArea?.categories.find((item) => item.name === category);
    return currentCategory?.subcategories || [];
  }, [area, category, categories]);
  const cartCount = cart.reduce((sum, item) => sum + item.quantity, 0);
  const cartTotal = cart.reduce((sum, item) => sum + item.priceCRC * item.quantity, 0);
  const pageCount = Math.max(1, Math.ceil(totalItems / pageSize));
  const t = (key, values) => translate(language, key, values);

  const loadProducts = useCallback(async (signal) => {
    setLoading(true);
    setError("");
    const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
    if (search) params.set("search", search);
    if (area) params.set("area", area);
    if (category) params.set("category", category);
    if (subcategory) params.set("subcategory", subcategory);
    try {
      const result = await request(`/products?${params}`, { signal });
      setProducts(result.items || []);
      setTotalItems(result.totalItems || 0);
    } catch (loadError) {
      if (loadError.name !== "AbortError") setError(translateError(languageRef.current, loadError.message));
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }, [area, category, page, search, subcategory]);

  useEffect(() => {
    function handleShortcut(event) {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        searchRef.current?.focus();
      }
    }
    window.addEventListener("keydown", handleShortcut);
    return () => window.removeEventListener("keydown", handleShortcut);
  }, []);

  useEffect(() => {
    const timeout = setTimeout(() => {
      setPage(1);
      setSearch(searchInput.trim());
    }, 350);
    return () => clearTimeout(timeout);
  }, [searchInput]);

  useEffect(() => {
    const controller = new AbortController();
    loadProducts(controller.signal);
    return () => controller.abort();
  }, [loadProducts]);

  useEffect(() => {
    const controller = new AbortController();
    request("/categories", { signal: controller.signal })
      .then((result) => setCategories(result))
      .catch((loadError) => {
        if (loadError.name !== "AbortError") setError((current) => current || translateError(language, loadError.message));
      });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    localStorage.setItem(CART_KEY, JSON.stringify(cart));
  }, [cart]);

  useEffect(() => {
    languageRef.current = language;
    localStorage.setItem(LANGUAGE_KEY, language);
    document.documentElement.lang = language;
  }, [language]);

  useEffect(() => {
    const token = localStorage.getItem(TOKEN_KEY);
    if (!token) return;
    const controller = new AbortController();
    request("/auth/me", { signal: controller.signal })
      .then(setUser)
      .catch((loadError) => {
        if (loadError.name === "AbortError") return;
        localStorage.removeItem(TOKEN_KEY);
        setUser(null);
      });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    setOffers([]);
    if (!user?.userId) return;
    const controller = new AbortController();
    request("/recommendations/offers", { signal: controller.signal })
      .then(setOffers)
      .catch((loadError) => {
        if (loadError.name !== "AbortError") setError(translateError(languageRef.current, loadError.message));
      });
    return () => controller.abort();
  }, [user?.userId]);

  function chooseArea(value) {
    setArea(value);
    setCategory("");
    setSubcategory("");
    setPage(1);
  }

  function addToCart(product) {
    if (product.stockQuantity <= 0) return;
    setCart((current) => {
      const exists = current.find((item) => item.barcode === product.barcode);
      if (exists) {
        return current.map((item) => item.barcode === product.barcode
          ? {
            ...item,
            ...(product.offerId ? {
              priceCRC: product.priceCRC,
              offerId: product.offerId,
              discountPercent: product.discountPercent,
            } : {}),
            quantity: Math.min(item.quantity + 1, product.stockQuantity || 99),
          }
          : item);
      }
      return [...current, {
        barcode: product.barcode,
        productName: product.productName,
        priceCRC: product.priceCRC,
        imageUrl: product.imageUrl,
        stockQuantity: product.stockQuantity,
        quantity: 1,
      }];
    });
    setNotice(t("notice.added", { name: product.productName }));
    setTimeout(() => setNotice(""), 2200);
  }

  function updateQuantity(barcode, change) {
    setCart((current) => current
      .map((item) => item.barcode === barcode
        ? { ...item, quantity: Math.min(item.quantity + change, item.stockQuantity || 99) }
        : item)
      .filter((item) => item.quantity > 0));
  }

  async function submitAuth(event) {
    event.preventDefault();
    setSubmittingAuth(true);
    setError("");
    try {
      const endpoint = authMode === "signup" ? "/auth/signup" : "/auth/login";
      const result = await request(endpoint, {
        method: "POST",
        body: JSON.stringify({ userName: username, password }),
      });
      localStorage.setItem(TOKEN_KEY, result.accessToken);
      setUser({ userId: result.userId, userName: result.userName });
      setPassword("");
      setAuthOpen(false);
      setNotice(t("notice.welcome", { name: result.userName }));
      setTimeout(() => setNotice(""), 2400);
    } catch (authError) {
      setError(translateError(language, authError.message));
    } finally {
      setSubmittingAuth(false);
    }
  }

  async function logout() {
    try {
      await request("/auth/logout", { method: "POST" });
    } catch {
      setError(t("error.logout"));
      return;
    }
    localStorage.removeItem(TOKEN_KEY);
    setUser(null);
    setOffers([]);
    setNotice(t("notice.signedOut"));
    setTimeout(() => setNotice(""), 2200);
  }

  async function checkout() {
    if (!user) {
      setAuthMode("login");
      setAuthOpen(true);
      return;
    }
    setCheckingOut(true);
    setError("");
    checkoutIdempotencyKey.current ||= crypto.randomUUID();
    try {
      await request("/payment/checkout", {
        method: "POST",
        body: JSON.stringify({
          idempotencyKey: checkoutIdempotencyKey.current,
          items: cart.map(({ barcode, quantity, offerId }) => ({ barcode, quantity, offerId })),
        }),
      });
      setCart([]);
      setCartOpen(false);
      checkoutIdempotencyKey.current = null;
      setNotice(t("notice.checkoutApproved"));
      setTimeout(() => setNotice(""), 2600);
      try {
        setOffers(await request("/recommendations/offers"));
      } catch (offersError) {
        setError(translateError(language, offersError.message));
      }
    } catch (checkoutError) {
      setError(translateError(language, checkoutError.message));
    } finally {
      setCheckingOut(false);
    }
  }

  return (
    <div className="app-shell">
      <header className="site-header">
        <a className="brand" href="#" aria-label="MarketDixon home">
          <span className="brand-copy">market<span>dixon</span><small>{t("brand.subtitle")}</small></span>
        </a>
        <label className="search-box">
          <Icon name="search" size={19} />
          <input ref={searchRef} value={searchInput} onChange={(event) => setSearchInput(event.target.value)} placeholder={t("search.placeholder")} aria-label={t("search.label")} />
          <kbd>{t("search.shortcut")}</kbd>
        </label>
        <div className="header-actions">
          <div className="language-switch" role="group" aria-label={t("language.label")}>
            <button className={language === "en" ? "active" : ""} aria-pressed={language === "en"} onClick={() => setLanguage("en")}>EN</button>
            <span aria-hidden="true">|</span>
            <button className={language === "es" ? "active" : ""} aria-pressed={language === "es"} onClick={() => setLanguage("es")}>ES</button>
          </div>
          {user
            ? <button className="account-button signed-in" onClick={logout} title={t("account.signOut")}><Icon name="user" /><span>{user.userName}</span></button>
            : <button className="account-button" onClick={() => { setAuthMode("login"); setAuthOpen(true); }}><Icon name="user" /><span>{t("account.signIn")}</span></button>}
          <button className="basket-button" onClick={() => setCartOpen(true)} aria-label={t("basket.open", { count: cartCount })}>
            <Icon name="cart" /><span>{t("basket.label")}</span><b>{cartCount}</b>
          </button>
        </div>
      </header>

      <main>
        {user && <div className="offers-wrap">
          <OfferSection title={t("offers.discovery")} offers={offers.filter((offer) => offer.kind === "discovery")} onAdd={addToCart} money={money} translate={t} />
          <OfferSection title={t("offers.favorites")} offers={offers.filter((offer) => offer.kind === "favorite")} onAdd={addToCart} money={money} translate={t} />
        </div>}
        <section className="shop-section" id="shop">
          <div className="section-heading">
            <div><span className="eyebrow section-kicker">{t("shop.eyebrow")}</span><h2>{t("shop.title.first")} <em>{t("shop.title.accent")}</em></h2></div>
            <p>{t("shop.description").split("|").map((line, index) => <React.Fragment key={line}>{index > 0 && <br />}{line}</React.Fragment>)}</p>
          </div>
          <div className="shop-toolbar">
            <button
              className="mobile-filter-toggle"
              aria-expanded={filtersOpen}
              aria-controls="product-filters"
              onClick={() => setFiltersOpen((open) => !open)}
            >
              {t("filter.toggle")} <span aria-hidden="true">{filtersOpen ? "−" : "+"}</span>
            </button>
            <div className={`filter-group${filtersOpen ? " is-open" : ""}`} id="product-filters">
              <label className="select-wrap"><span className="sr-only">{t("filter.area.label")}</span><select aria-label={t("filter.area.label")} value={area} onChange={(event) => chooseArea(event.target.value)}><option value="">{t("filter.area.all")}</option>{areaOptions.map((item) => <option key={item} value={item}>{item}</option>)}</select></label>
              <label className="select-wrap"><span className="sr-only">{t("filter.category.label")}</span><select aria-label={t("filter.category.label")} value={category} onChange={(event) => { setCategory(event.target.value); setSubcategory(""); setPage(1); }} disabled={!area}><option value="">{t("filter.category.all")}</option>{categoryOptions.map((item) => <option key={item} value={item}>{item}</option>)}</select></label>
              <label className="select-wrap"><span className="sr-only">{t("filter.subcategory.label")}</span><select aria-label={t("filter.subcategory.label")} value={subcategory} onChange={(event) => { setSubcategory(event.target.value); setPage(1); }} disabled={!category}><option value="">{t("filter.subcategory.all")}</option>{subcategoryOptions.map((item) => <option key={item} value={item}>{item}</option>)}</select></label>
            </div>
            <span className="result-count">{t("shop.results", { count: totalItems.toLocaleString(language === "es" ? "es-CR" : "en-US") })}</span>
          </div>

          {error && <div className="error-banner" role="alert"><span>{error}</span><button onClick={() => { setError(""); loadProducts(); }}>{t("shop.tryAgain")}</button></div>}
          {loading ? <div className="product-grid">{Array.from({ length: 8 }, (_, index) => <div className="skeleton-card" key={index}><div></div><span></span><span></span></div>)}</div>
            : products.length
              ? <div className="product-grid">{products.map((product, index) => <article className={`product-card tone-${index % 5}`} key={product.barcode}>
                <div className="product-image-wrap">
                  <span className="product-chip">{product.priceIsEstimated ? t("product.priceEstimate") : t("product.localPick")}</span>
                  {product.imageUrl ? <img className="product-image" src={product.imageUrl} alt={product.productName} loading="lazy" onError={(event) => { event.currentTarget.style.display = "none"; event.currentTarget.nextElementSibling.style.display = "grid"; }} /> : null}
                  <div className="image-placeholder" style={{ display: product.imageUrl ? "none" : "grid" }}><Icon name="leaf" size={34} /><span>{t("product.placeholder")}</span></div>
                  <button className="quick-add" onClick={() => addToCart(product)} disabled={product.stockQuantity <= 0} aria-label={t("product.addToBasket", { name: product.productName })} title={product.stockQuantity > 0 ? t("product.addTitle") : t("product.outOfStock")}><Icon name="plus" size={20} /></button>
                </div>
                <div className="product-info"><span className="product-category">{product.category || product.area || t("product.marketFavorite")}</span><h3>{product.productName}</h3><p>{[product.brand, product.packageQuantity].filter(Boolean).join(" · ") || t("product.everydayGoodness")}</p><div className="product-price-row"><strong>{money.format(product.priceCRC)}</strong><span>{product.stockQuantity > 0 ? t("product.inStock") : t("product.outOfStock")}</span></div></div>
              </article>)}</div>
              : <div className="empty-state"><span>✿</span><h3>{t("shop.empty.title")}</h3><p>{t("shop.empty.description")}</p><button onClick={() => { setSearchInput(""); setArea(""); setCategory(""); setSubcategory(""); }}>{t("shop.empty.reset")}</button></div>}

          {!loading && products.length > 0 && <div className="pagination"><span>{t("shop.showing", { start: ((page - 1) * pageSize + 1).toLocaleString(language === "es" ? "es-CR" : "en-US"), end: Math.min(page * pageSize, totalItems).toLocaleString(language === "es" ? "es-CR" : "en-US"), total: totalItems.toLocaleString(language === "es" ? "es-CR" : "en-US") })}</span><div><button disabled={page <= 1} aria-label={t("pagination.previous")} onClick={() => { setPage((value) => value - 1); document.getElementById("shop").scrollIntoView({ behavior: "smooth" }); }}>←</button><b>{page} / {pageCount}</b><button disabled={page >= pageCount} aria-label={t("pagination.next")} onClick={() => { setPage((value) => value + 1); document.getElementById("shop").scrollIntoView({ behavior: "smooth" }); }}><Icon name="arrow" size={17} /></button></div></div>}
        </section>

        <section className="promise-band"><div><span className="promise-icon">✦</span><p><b>{t("promise.title")}</b><br />{t("promise.description")}</p></div><span>{t("promise.caption")} <i>✿</i></span></section>
      </main>
      <footer><a className="brand footer-brand" href="#"><span className="brand-copy">market<span>dixon</span><small>{t("brand.subtitle")}</small></span></a><p>{t("footer.caption")} <span>© {new Date().getFullYear()} MarketDixon</span></p><a href="#shop">{t("footer.backToShop")}</a></footer>

      {notice && <div className="toast" role="status"><span>✓</span>{notice}</div>}

      {cartOpen && <div className="overlay" onMouseDown={(event) => { if (event.target === event.currentTarget) setCartOpen(false); }}>
        <aside className="drawer" aria-label={t("basket.label")}>
          <div className="drawer-heading"><div><span className="eyebrow">{t("cart.title")}</span><h2>{t("cart.heading")} <span>({cartCount})</span></h2></div><button className="icon-button" onClick={() => setCartOpen(false)} aria-label={t("cart.close")}><Icon name="close" /></button></div>
          {cart.length ? <><div className="cart-items">{cart.map((item) => <div className="cart-item" key={item.barcode}><div className="cart-thumb">{item.imageUrl ? <img src={item.imageUrl} alt="" /> : <Icon name="leaf" />}</div><div className="cart-item-info"><b>{item.productName}</b><span>{money.format(item.priceCRC)}</span><div className="quantity-control"><button onClick={() => updateQuantity(item.barcode, -1)} aria-label={t("cart.decrease")}>−</button><span>{item.quantity}</span><button onClick={() => updateQuantity(item.barcode, 1)} aria-label={t("cart.increase")}>+</button></div></div><strong>{money.format(item.priceCRC * item.quantity)}</strong></div>)}</div><div className="cart-summary"><div><span>{t("cart.subtotal")}</span><b>{money.format(cartTotal)}</b></div><small>{t("cart.mockPayment")}</small><button className="checkout-button" onClick={checkout} disabled={checkingOut}>{checkingOut ? t("cart.checkout.loading") : user ? t("cart.checkout.pay", { total: money.format(cartTotal) }) : t("cart.checkout.signIn")}<Icon name="arrow" size={18} /></button></div></>
            : <div className="cart-empty"><span>🛍</span><h3>{t("cart.empty.title")}</h3><p>{t("cart.empty.description")}</p><button onClick={() => setCartOpen(false)}>{t("cart.empty.continue")}</button></div>}
        </aside>
      </div>}

      {authOpen && <div className="overlay auth-overlay" onMouseDown={(event) => { if (event.target === event.currentTarget) setAuthOpen(false); }}>
        <section className="auth-card">
          <button className="icon-button auth-close" onClick={() => setAuthOpen(false)} aria-label={t("auth.close")}><Icon name="close" /></button>
          <span className="eyebrow">{t("auth.eyebrow")}</span>
          <h2>{authMode === "login" ? t("auth.login.title") : t("auth.signup.title")}</h2><p>{authMode === "login" ? t("auth.login.description") : t("auth.signup.description")}</p>
          <form onSubmit={submitAuth}><label>{t("auth.username")}<input autoComplete="username" value={username} onChange={(event) => setUsername(event.target.value)} required minLength={3} maxLength={50} /></label><label>{t("auth.password")}<input type="password" autoComplete={authMode === "login" ? "current-password" : "new-password"} value={password} onChange={(event) => setPassword(event.target.value)} required minLength={authMode === "signup" ? 8 : 1} maxLength={128} /></label>{error && <div className="auth-error" role="alert">{error}</div>}<button className="checkout-button" type="submit" disabled={submittingAuth}>{submittingAuth ? t("auth.loading") : authMode === "login" ? t("account.signIn") : t("auth.create")}<Icon name="arrow" size={18} /></button></form>
          <div className="auth-switch">{authMode === "login" ? t("auth.newHere") : t("auth.existing")} <button onClick={() => { setAuthMode(authMode === "login" ? "signup" : "login"); setError(""); }}>{authMode === "login" ? t("auth.switchToSignup") : t("auth.switchToLogin")}</button></div>
        </section>
      </div>}
    </div>
  );
}

export default App;

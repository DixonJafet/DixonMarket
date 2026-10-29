using System.Data;
using System.Security.Claims;
using System.Text.RegularExpressions;
using MarketDixon.Api;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
var allowedOrigins = (builder.Configuration["Cors:AllowedOrigins"]
        ?? Environment.GetEnvironmentVariable("MARKETDIXON_CORS_ORIGINS")
        ?? "http://localhost:5173;http://127.0.0.1:5173;https://dixonmarket.vercel.app;https://vercel.com")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()));
var connectionString = DatabaseConfiguration.GetConnectionString(builder.Configuration, builder.Environment);
var tokenSigningKey = TokenService.GetSigningKey(builder.Configuration, builder.Environment);
var database = new ProductDatabase(connectionString);
var recommendationDatabase = new RecommendationDatabase(connectionString);
var passwordHasher = new PasswordHasher();
var tokenService = new TokenService(tokenSigningKey);

builder.Services.AddSingleton(database);
builder.Services.AddSingleton(recommendationDatabase);
builder.Services.AddSingleton(passwordHasher);
builder.Services.AddSingleton(tokenService);
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors();
app.Use(async (context, next) =>
{
    var authorization = context.Request.Headers.Authorization.ToString();
    if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        var claims = tokenService.Validate(authorization["Bearer ".Length..].Trim());
        if (claims is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired bearer token." });
            return;
        }

        if (await database.IsTokenRevokedAsync(TokenService.HashToken(authorization["Bearer ".Length..].Trim()), context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired bearer token." });
            return;
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    await next();
});

await database.InitializeAsync(app.Lifetime.ApplicationStopping);
await recommendationDatabase.InitializeAsync(app.Lifetime.ApplicationStopping);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/auth/signup", async (SignupRequest request, CancellationToken cancellationToken) =>
{
    var username = request.UserName?.Trim();
    if (!IsValidUserName(username))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["userName"] = ["User name must be 3-50 characters and contain only letters, numbers, '.', '_' or '-'. "]
        });
    }
    var validUserName = username!;

    if (request.Password is null || request.Password.Length is < 8 or > 128)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["password"] = ["Password must be between 8 and 128 characters."]
        });
    }

    var userId = Guid.NewGuid();
    var normalizedUserName = validUserName.ToUpperInvariant();
    var passwordHash = passwordHasher.Hash(request.Password);
    try
    {
        await database.CreateUserAsync(userId, validUserName, normalizedUserName, passwordHash, cancellationToken);
    }
    catch (SqlException exception) when (exception.Number is 2601 or 2627)
    {
        return Results.Conflict(new { error = "That user name is already registered." });
    }

    return Results.Created("/api/auth/me", new AuthResponse(
        userId,
        validUserName,
        tokenService.Create(userId, validUserName)));
});

app.MapPost("/api/auth/login", async (LoginRequest request, CancellationToken cancellationToken) =>
{
    var username = request.UserName?.Trim();
    if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(request.Password))
    {
        return Results.Unauthorized();
    }

    var user = await database.FindUserAsync(username.ToUpperInvariant(), cancellationToken);
    if (user is null || !passwordHasher.Verify(request.Password, user.PasswordHash))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new AuthResponse(user.UserId, user.UserName, tokenService.Create(user.UserId, user.UserName)));
});

app.MapGet("/api/auth/me", async (ClaimsPrincipal principal, CancellationToken cancellationToken) =>
{
    if (!TryGetUserId(principal, out var userId))
    {
        return Results.Unauthorized();
    }

    var user = await database.FindUserByIdAsync(userId, cancellationToken);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(new UserResponse(user.UserId, user.UserName));
});

app.MapPost("/api/auth/logout", async (
    HttpContext context,
    ClaimsPrincipal principal,
    CancellationToken cancellationToken) =>
{
    if (!TryGetUserId(principal, out _))
    {
        return Results.Unauthorized();
    }

    var authorization = context.Request.Headers.Authorization.ToString();
    var token = authorization["Bearer ".Length..].Trim();
    var expiresAtClaim = principal.FindFirstValue("token_expires_at");
    if (!long.TryParse(expiresAtClaim, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var expiresAt))
    {
        return Results.Unauthorized();
    }

    await database.RevokeTokenAsync(
        TokenService.HashToken(token),
        DateTimeOffset.FromUnixTimeSeconds(expiresAt).UtcDateTime,
        cancellationToken);
    return Results.Ok(new { message = "Logged out successfully." });
});

app.MapGet("/api/products", async (
    int? page,
    int? pageSize,
    string? search,
    string? area,
    string? category,
    string? subcategory,
    CancellationToken cancellationToken) =>
{
    var pageNumber = page ?? 1;
    var pageLength = pageSize ?? 20;
    if (pageNumber < 1 || pageLength is < 1 or > 100)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["pagination"] = ["Page must be positive and pageSize must be between 1 and 100."]
        });
    }

    var result = await database.GetProductsAsync(
        pageNumber,
        pageLength,
        search,
        area,
        category,
        subcategory,
        cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/api/products/{barcode}", async (string barcode, CancellationToken cancellationToken) =>
{
    var product = await database.GetProductAsync(barcode, cancellationToken);
    return product is null ? Results.NotFound() : Results.Ok(product);
});

app.MapGet("/api/categories", async (CancellationToken cancellationToken) =>
    Results.Ok(await database.GetCategoriesAsync(cancellationToken)));

app.MapPost("/api/payment/checkout", async (
    ClaimsPrincipal principal,
    CheckoutRequest request,
    CancellationToken cancellationToken) =>
{
    if (!TryGetUserId(principal, out var userId))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(await recommendationDatabase.CheckoutAsync(userId, request, cancellationToken));
    }
    catch (InvalidCheckoutException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (IdempotencyConflictException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
});

app.MapGet("/api/recommendations/offers", async (
    ClaimsPrincipal principal,
    CancellationToken cancellationToken) =>
{
    if (!TryGetUserId(principal, out var userId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await recommendationDatabase.GetOffersAsync(userId, cancellationToken));
});

app.MapGet("/api/recommendations/profile", async (
    ClaimsPrincipal principal,
    CancellationToken cancellationToken) =>
{
    if (!TryGetUserId(principal, out var userId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await recommendationDatabase.GetProfileAsync(userId, cancellationToken));
});

app.Run();

static bool IsValidUserName(string? username) =>
    username is { Length: >= 3 and <= 50 } &&
    Regex.IsMatch(username, @"^[\p{L}\p{N}._-]+$", RegexOptions.CultureInvariant);

static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId) =>
    Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

public partial class Program;

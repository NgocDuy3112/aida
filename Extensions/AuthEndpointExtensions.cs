using System.Security.Claims;
using aida.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace aida.Extensions;

public static class AuthEndpointExtensions
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app, IHostEnvironment env)
    {
        app.MapGet("/account/external-login",
            (string? provider, string? returnUrl, SignInManager<AppUser> signInManager, IConfiguration config) =>
        {
            if (!string.Equals(provider ?? "Google", "Google", StringComparison.OrdinalIgnoreCase))
                return Results.Redirect("/login?error=provider");

            if (!IdentityExtensions.IsGoogleConfigured(config))
                return Results.Redirect("/login?error=notconfigured");

            var redirect =
                $"/account/external-login-callback?returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}";
            return Results.Challenge(
                signInManager.ConfigureExternalAuthenticationProperties("Google", redirect),
                ["Google"]);
        }).AllowAnonymous();

        app.MapGet("/account/external-login-callback", async (
            string? returnUrl, SignInManager<AppUser> signInManager, UserManager<AppUser> userManager,
            RoleManager<IdentityRole> roleManager, IConfiguration config,
            ILogger<Program> logger) =>
        {
            var target = SafeReturnUrl(returnUrl);

            var info = await signInManager.GetExternalLoginInfoAsync();
            if (info is null) return Results.Redirect("/login?error=external");

            var signIn = await signInManager.ExternalLoginSignInAsync(
                info.LoginProvider, info.ProviderKey, isPersistent: true, bypassTwoFactor: true);
            if (signIn.Succeeded)
            {
                var returning = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
                if (returning is not null)
                {
                    await SyncExternalProfileAsync(userManager, info, returning);
                    await EnsureAdminAsync(userManager, roleManager, config, returning);
                    await PersistGoogleRefreshTokenAsync(signInManager, userManager, returning);
                }
                return Results.Redirect(target);
            }

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            var verified = string.Equals(info.Principal.FindFirstValue("email_verified"), "true",
                                         StringComparison.OrdinalIgnoreCase);
            if (email is null || !verified)
            {
                logger.LogWarning(
                    "Google login rejected: email={Email} verified={Verified} claims={Claims}",
                    email, verified,
                    string.Join(", ", info.Principal.Claims.Select(c => c.Type)));
                return Results.Redirect("/login?error=email");
            }

            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new AppUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    DisplayName = info.Principal.FindFirstValue(ClaimTypes.Name),
                    AvatarUrl = info.Principal.FindFirstValue("picture"),
                };
                var created = await userManager.CreateAsync(user);
                if (!created.Succeeded) return Results.Redirect("/login?error=create");
            }
            else if (!await userManager.IsEmailConfirmedAsync(user))
            {
                return Results.Redirect("/login?error=unconfirmed");
            }

            var linked = await userManager.AddLoginAsync(user, info);
            if (!linked.Succeeded) return Results.Redirect("/login?error=link");

            await SyncExternalProfileAsync(userManager, info, user);
            await EnsureAdminAsync(userManager, roleManager, config, user);
            await PersistGoogleRefreshTokenAsync(signInManager, userManager, user);

            await signInManager.SignInAsync(user, isPersistent: true);
            return Results.Redirect(target);
        }).AllowAnonymous();

        app.MapPost("/account/signout", async (SignInManager<AppUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.Redirect("/login");
        }).RequireAuthorization();

        app.MapPost("/account/google/disconnect", async (
            ClaimsPrincipal principal, UserManager<AppUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is not null)
            {
                user.GoogleRefreshToken = null;
                user.GoogleRefreshTokenObtainedUtc = null;
                await userManager.UpdateAsync(user);
            }
            return Results.Redirect("/");
        }).RequireAuthorization();

        app.MapPost("/account/password-signin", async (
            HttpContext context, SignInManager<AppUser> signInManager,
            UserManager<AppUser> userManager) =>
        {
            var form = await context.Request.ReadFormAsync();
            var email = form["email"].ToString().Trim();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            var result = await signInManager.PasswordSignInAsync(
                email, password, isPersistent: true, lockoutOnFailure: true);

            if (result.Succeeded)
                return Results.Redirect(SafeReturnUrl(returnUrl));

            if (result.IsLockedOut)
                return Results.Redirect("/login?error=lockedout");

            var existing = await userManager.FindByEmailAsync(email);
            if (existing is not null)
            {
                if (string.IsNullOrEmpty(existing.PasswordHash))
                    return Results.Redirect("/login?error=googleonly");
            }

            return Results.Redirect("/login?error=invalidcredentials");
        }).AllowAnonymous();

        app.MapPost("/account/password-register", async (
            HttpContext context, UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager) =>
        {
            var form = await context.Request.ReadFormAsync();
            var email = form["email"].ToString().Trim();
            var password = form["password"].ToString();
            var confirm = form["confirm"].ToString();

            if (string.IsNullOrEmpty(email))
                return Results.Redirect("/register?error=invalid");

            if (string.IsNullOrEmpty(password))
                return Results.Redirect("/register?error=invalid");

            if (password != confirm)
                return Results.Redirect("/register?error=mismatch");

            var displayName = email;
            var at = email.IndexOf('@');
            if (at > 0)
                displayName = email[..at];

            var user = new AppUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = displayName,
            };

            var created = await userManager.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                var takenName = created.Errors.Any(e => e.Code == "DuplicateUserName");
                var takenEmail = created.Errors.Any(e => e.Code == "DuplicateEmail");
                if (takenName)
                    return Results.Redirect("/register?error=taken");
                if (takenEmail)
                    return Results.Redirect("/register?error=taken");
                return Results.Redirect("/register?error=weak");
            }

            await signInManager.SignInAsync(user, isPersistent: true);
            return Results.Redirect("/");
        }).AllowAnonymous();

        if (env.IsDevelopment())
        {
            app.MapGet("/dev-login", async (
                SignInManager<AppUser> signInManager, UserManager<AppUser> userManager,
                RoleManager<IdentityRole> roleManager) =>
            {
                const string email = "dev@localhost";
                var user = await userManager.FindByEmailAsync(email);
                if (user is null)
                {
                    user = new AppUser
                    {
                        UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Dev User"
                    };
                    await userManager.CreateAsync(user);
                }

                if (!await roleManager.RoleExistsAsync("Admin"))
                    await roleManager.CreateAsync(new IdentityRole("Admin"));
                if (!await userManager.IsInRoleAsync(user, "Admin"))
                    await userManager.AddToRoleAsync(user, "Admin");

                await signInManager.SignInAsync(user, isPersistent: true);
                return Results.Redirect("/");
            }).AllowAnonymous();
        }

        return app;
    }

    internal static async Task SyncExternalProfileAsync(
        UserManager<AppUser> userManager, ExternalLoginInfo info, AppUser user)
    {
        var name = info.Principal.FindFirstValue(ClaimTypes.Name);
        var picture = info.Principal.FindFirstValue("picture");

        var changed = false;
        if (!string.IsNullOrWhiteSpace(name) && user.DisplayName != name)
        {
            user.DisplayName = name;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(picture) && user.AvatarUrl != picture)
        {
            user.AvatarUrl = picture;
            changed = true;
        }

        if (changed)
            await userManager.UpdateAsync(user);
    }

    internal static async Task PersistGoogleRefreshTokenAsync(
        SignInManager<AppUser> signInManager, UserManager<AppUser> userManager, AppUser user)
    {
        var external = await signInManager.Context.AuthenticateAsync(IdentityConstants.ExternalScheme);
        var refresh = external.Properties?.GetTokenValue("refresh_token");
        if (string.IsNullOrWhiteSpace(refresh) || user.GoogleRefreshToken == refresh)
            return;

        user.GoogleRefreshToken = refresh;
        user.GoogleRefreshTokenObtainedUtc = DateTime.UtcNow;
        await userManager.UpdateAsync(user);
    }

    internal static bool IsAdminEmail(IConfiguration config, string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;

        return config.GetSection("Admin:Emails").GetChildren()
            .Select(v => v.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Contains(email, StringComparer.OrdinalIgnoreCase);
    }

    internal static async Task EnsureAdminAsync(
        UserManager<AppUser> userManager, RoleManager<IdentityRole> roleManager,
        IConfiguration config, AppUser user)
    {
        if (!IsAdminEmail(config, user.Email)) return;

        if (!await roleManager.RoleExistsAsync("Admin"))
            await roleManager.CreateAsync(new IdentityRole("Admin"));
        if (!await userManager.IsInRoleAsync(user, "Admin"))
            await userManager.AddToRoleAsync(user, "Admin");
    }

    internal static string SafeReturnUrl(string? returnUrl)
        => !string.IsNullOrEmpty(returnUrl)
           && returnUrl[0] == '/'
           && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
            ? returnUrl
            : "/";
}
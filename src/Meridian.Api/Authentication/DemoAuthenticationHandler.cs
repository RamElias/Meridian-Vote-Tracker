using System.Security.Claims;
using System.Text.Encodings.Web;
using Meridian.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Meridian.Api.Authentication;

// Assignment identity adapter. Replace this scheme with the company's identity provider in production.
public sealed class DemoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, DemoUserService users) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Demo";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers["X-Demo-User"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(userId)) return AuthenticateResult.NoResult();
        var user = await users.FindAsync(userId, Context.RequestAborted);
        if (user is null) return AuthenticateResult.Fail("Unknown demo user.");
        var identity = new ClaimsIdentity(new[] {
            new Claim(ClaimTypes.NameIdentifier, user.UserId),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role)
        }, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Results.Problem(title: "Authentication required", detail: "Choose a seeded demo user (dana, omer, or lana) in X-Demo-User.", statusCode: 401).ExecuteAsync(Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Results.Problem(title: "Administrator required", statusCode: 403).ExecuteAsync(Context);
}

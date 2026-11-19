using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DeficitSnatcher.Api.Auth;

/// <summary>
/// One owner, signed in with the username and password from settings (Auth__Username, Auth__Password); the web keeps
/// the token it gets back. Every other endpoint needs it.
/// </summary>
public sealed class AuthOptions
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Issuer { get; set; } = "deficitsnatcher-api";
    public string Audience { get; set; } = "deficitsnatcher-web";
    public string SigningKey { get; set; } = "";
    public int TokenDays { get; set; } = 14;

    public SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(SigningKey));

    public void Validate()
    {
        if (SigningKey.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters (openssl rand -base64 48).");
        if (Username.Length == 0 || Password.Length < 8)
            throw new InvalidOperationException("Auth:Username and Auth:Password (8+ characters) must be set.");
    }
}

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, string Username);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthOptions options) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public ActionResult<LoginResponse> Login(LoginRequest request)
    {
        if (!Same(request.Username ?? "", options.Username) | !Same(request.Password ?? "", options.Password))
            return Problem("Wrong username or password.", statusCode: StatusCodes.Status401Unauthorized);

        var expires = DateTimeOffset.UtcNow.AddDays(options.TokenDays);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Subject = new ClaimsIdentity([new Claim("sub", options.Username)]),
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(options.Key, SecurityAlgorithms.HmacSha256),
        });
        return new LoginResponse(token, expires, options.Username);
    }

    [HttpGet("me")]
    public ActionResult<object> Me() => new { username = User.FindFirst("sub")?.Value };

    private static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}

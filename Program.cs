using System.Text;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Options;
using CompetitionManagementSystem.Security;
using CompetitionManagementSystem.Services;
using CompetitionManagementSystem.Services.Ingestion;
using CompetitionManagementSystem.Services.Mfa;
using CompetitionManagementSystem.Services.Reporting;
using CompetitionManagementSystem.Services.Scoring;
using CompetitionManagementSystem.Services.Seed;
using CompetitionManagementSystem.Services.Twitter;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi; // الصحيح في .NET 10

var builder = WebApplication.CreateBuilder(args);

// ================= JWT CONFIG =================
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("JwtSettings"));
builder.Services.Configure<TwitterApiIoOptions>(builder.Configuration.GetSection("TwitterApiIo"));
builder.Services.Configure<AdminSecurityOptions>(builder.Configuration.GetSection("AdminSecurity"));

var jwtSettings = builder.Configuration.GetSection("JwtSettings").Get<JwtSettings>() ?? new JwtSettings();

if (string.IsNullOrWhiteSpace(jwtSettings.SecretKey) || jwtSettings.SecretKey.Length < 32)
{
    throw new InvalidOperationException("JwtSettings:SecretKey must be configured and at least 32 characters long.");
}

// ================= DATABASE =================
builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
});

// ================= IDENTITY =================
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        // Identity-level password policy (secondary; primary enforcement is PasswordPolicy.Validate
        // in the register/create/reset endpoints).
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;

        // Lockout: 3 failed attempts -> account locked for 1 minute.
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 3;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(1);
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

// ================= JWT AUTH =================
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
            // Security requirement: exact session timeout. Default 5-minute skew would let
            // tokens be accepted past their configured ExpiryMinutes — set to zero.
            ClockSkew = TimeSpan.Zero
        };

        // Prevent IIS Basic-Auth popup: suppress WWW-Authenticate header on 401 challenge.
        // IIS intercepts any 401 that carries WWW-Authenticate and triggers browser popup.
        // We return a JSON body and clear the header so IIS has nothing to intercept.
        options.Events = new JwtBearerEvents
        {
            OnChallenge = ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode  = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "application/json";
                return ctx.Response.WriteAsync("{\"message\":\"Unauthorized. Please provide a valid token.\"}");
            }
        };
    });

builder.Services.AddAuthorization();

// ================= RATE LIMITING =================
// Global default: 100 req/min per client (IP, or authenticated user id if available).
// Stricter "auth" policy: 10 req/min — applied to login/register endpoints in AuthController.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var key = httpContext.User?.Identity?.IsAuthenticated == true
            ? ("user:" + (httpContext.User!.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "unknown"))
            : ("ip:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"));

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("auth", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter("authip:" + ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            "{\"error\":\"Too many requests. Please slow down and try again later.\"}",
            cancellationToken);
    };
});

// ================= CORS =================
builder.Services.AddCors(options =>
{
    options.AddPolicy("ConfiguredCors", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// ================= HTTP CLIENT =================
builder.Services.AddHttpClient<TwitterApiIoSearchClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TwitterApiIoOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/'));
});

// ================= SERVICES =================
builder.Services.AddScoped<MockTwitterSearchClient>();
builder.Services.AddScoped<ITwitterSearchClient>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TwitterApiIoOptions>>().Value;
    return options.UseMock
        ? sp.GetRequiredService<MockTwitterSearchClient>()
        : sp.GetRequiredService<TwitterApiIoSearchClient>();
});

builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IIngestionService, IngestionService>();
builder.Services.AddScoped<IScoringService, ScoringService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IGenderInferenceService, GenderInferenceService>();
builder.Services.AddHostedService<TwitterIngestionBackgroundService>();

// Data Protection: keys persisted to a non-public folder under the content root.
// TotpService uses this to encrypt TOTP secrets and recovery codes at rest.
var dpKeyFolder = Path.Combine(builder.Environment.ContentRootPath, "DataProtectionKeys");
Directory.CreateDirectory(dpKeyFolder);
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeyFolder))
    .SetApplicationName("CompetitionManagementSystem");

builder.Services.AddSingleton<IClientIpResolver, ClientIpResolver>();
builder.Services.AddScoped<ITotpService, TotpService>();
builder.Services.AddScoped<IMfaTicketService, MfaTicketService>();

// ================= CONTROLLERS =================
builder.Services.AddControllers();

// ================= SWAGGER (FIXED) =================
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "CompetitionManagementSystem",
        Version = "v1"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Enter JWT Token",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    options.AddSecurityRequirement(document =>
        new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecuritySchemeReference("Bearer", document),
                new List<string>() // الحل النهائي للخطأ
            }
        });
});

// ================= BUILD =================
var app = builder.Build();

// ================= SEED =================
await DatabaseSeeder.SeedAsync(app.Services, app.Configuration);

// ================= MIDDLEWARE =================
app.UseHttpsRedirection();

app.UseCors("ConfiguredCors");

// Endpoint routing must be explicit so AdminIpRestrictionMiddleware can read
// the matched endpoint's authorization metadata.
app.UseRouting();

// Admin IP restriction runs BEFORE authentication so blocked clients never
// reach token validation, identity lookup, or rate-limit decisions.
app.UseMiddleware<AdminIpRestrictionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.UseRateLimiter();

app.MapControllers();

app.UseSwagger();
app.UseSwaggerUI();

app.Run();
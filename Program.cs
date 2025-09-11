﻿// path: honey_badger_api/Program.cs
using DotNetEnv;
using honey_badger_api.Abstractions;
using honey_badger_api.Data;
using honey_badger_api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

// ─────────────────────────────────────────────────────────────────────────────
// 1) Load .env (first), then build configuration
//    - Env vars can override appsettings.*
// ─────────────────────────────────────────────────────────────────────────────
try
{
    // Try to find the nearest .env walking up from CWD; fall back to local load.
    Env.TraversePath().Load();
}
catch
{
    // If TraversePath fails silently, attempt a direct load (optional).
    try { Env.Load(); } catch { /* ignore */ }
}

var builder = WebApplication.CreateBuilder(args);

// Make sure environment variables are considered (added by default, but explicit is fine)
builder.Configuration.AddEnvironmentVariables();

// ─────────────────────────────────────────────────────────────────────────────
// 2) Database + Identity
//    Connection string precedence:
//      ENV: ConnectionStrings__DefaultConnection
//      ENV: DefaultConnection
//      appsettings.json: ConnectionStrings:DefaultConnection
// ─────────────────────────────────────────────────────────────────────────────
var connectionString =
    Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? Environment.GetEnvironmentVariable("DefaultConnection")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Missing DB connection string (DefaultConnection).");

builder.Services.AddDbContext<AppDbContext>(opt =>
{
    opt.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
});

builder.Services
    .AddIdentity<AppUser, IdentityRole>(opt =>
    {
        // Keep your defaults; tweak as desired
        opt.User.RequireUniqueEmail = true;
        opt.Password.RequiredLength = 6;
        opt.Password.RequireDigit = false;
        opt.Password.RequireUppercase = false;
        opt.Password.RequireLowercase = false;
        opt.Password.RequireNonAlphanumeric = false;
        opt.SignIn.RequireConfirmedEmail = false; // flip to true if/when you wire mail confirmation
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// ─────────────────────────────────────────────────────────────────────────────
// 3) JWT Auth
//    Reads from ENV first, then appsettings:
//      Jwt:Issuer, Jwt:Audience, Jwt:Key, Jwt:ExpiresMinutes
//    ENV fallbacks also support uppercase with double underscore, e.g. JWT__KEY
// ─────────────────────────────────────────────────────────────────────────────
string ReadJwt(string key, string altEnv) =>
    Environment.GetEnvironmentVariable($"Jwt__{key}") ??
    Environment.GetEnvironmentVariable(altEnv) ??
    builder.Configuration[$"Jwt:{key}"] ??
    "";

var jwtIssuer = ReadJwt("Issuer", "JWT__ISSUER");
var jwtAudience = ReadJwt("Audience", "JWT__AUDIENCE");
var jwtKey = ReadJwt("Key", "JWT__KEY");
var jwtExpStr = ReadJwt("ExpiresMinutes", "JWT__EXPIRESMINUTES");
var jwtExpires = int.TryParse(jwtExpStr, out var exp) ? exp : 120;

if (string.IsNullOrWhiteSpace(jwtKey))
    throw new InvalidOperationException("JWT signing key is missing. Set Jwt:Key (or env JWT__KEY).");

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(o =>
    {
        o.RequireHttpsMetadata = false; // set true in production behind HTTPS/Proxy
        o.SaveToken = true;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrWhiteSpace(jwtIssuer),
            ValidateAudience = !string.IsNullOrWhiteSpace(jwtAudience),
            ValidateIssuerSigningKey = true,
            ValidIssuer = string.IsNullOrWhiteSpace(jwtIssuer) ? null : jwtIssuer,
            ValidAudience = string.IsNullOrWhiteSpace(jwtAudience) ? null : jwtAudience,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

// ─────────────────────────────────────────────────────────────────────────────
// 4) Email services (your existing registrations)
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IAppEmailSender, MailjetEmailSender>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender, IdentityEmailSenderAdapter>();

// ─────────────────────────────────────────────────────────────────────────────
// 5) Controllers + Swagger (with Bearer setup)
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Portfolio API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme."
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// 6) CORS
//    Read from env `Cors__Origins` or `CORS__ORIGINS` or appsettings Cors:Origins
//    (semicolon separated)
// ─────────────────────────────────────────────────────────────────────────────
string corsOriginsRaw =
    Environment.GetEnvironmentVariable("Cors__Origins")
    ?? Environment.GetEnvironmentVariable("CORS__ORIGINS")
    ?? builder.Configuration["Cors:Origins"]
    ?? "";

var corsOrigins = corsOriginsRaw
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(opt =>
{
    opt.AddPolicy("spa", p =>
    {
        if (corsOrigins.Length > 0)
            p.WithOrigins(corsOrigins).AllowCredentials();
        else
            p.AllowAnyOrigin(); // dev-friendly fallback (no credentials)

        p.AllowAnyHeader()
         .AllowAnyMethod();
    });
});



// ─────────────────────────────────────────────────────────────────────────────
// Samsung-Data paths (FIX: use builder.Environment; not 'app' yet)
// ─────────────────────────────────────────────────────────────────────────────
var defaultRoot = Path.Combine(builder.Environment.ContentRootPath, "Samsung-Data");
var shealthRoot =
    Environment.GetEnvironmentVariable("SAMSUNG_DATA_DIR")
    ?? builder.Configuration["SAMSUNG_DATA_DIR"]
    ?? defaultRoot;

Directory.CreateDirectory(shealthRoot);
Directory.CreateDirectory(Path.Combine(shealthRoot, "ZIP_FILES"));
Directory.CreateDirectory(Path.Combine(shealthRoot, "RAW_DATA"));

builder.Services.AddSingleton(new ShealthConfig
{
    RootDir = shealthRoot
});
// Allow large multipart/form uploads
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 1_500_000_000; // ~1.5GB
    o.ValueLengthLimit = int.MaxValue;
    o.MultipartHeadersLengthLimit = int.MaxValue;
});

// Also bump Kestrel body size (only matters if not behind IIS)
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 1_500_000_000;
});

builder.Services.AddHostedService<honey_badger_api.Services.TelemetryRollupService>();
var app = builder.Build();

// 7) Static files + GLB models (unchanged except we now have 'app')
var glbDir =
    Environment.GetEnvironmentVariable("ANIMATIONS_GLB_DIR")
    ?? builder.Configuration["ANIMATIONS_GLB_DIR"]
    ?? Path.Combine(app.Environment.ContentRootPath, "badger_animation_glb");

app.Logger.LogInformation("GLB dir resolved to: {dir} (exists={exists})", glbDir, Directory.Exists(glbDir));

var webRoot = app.Environment.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
Directory.CreateDirectory(Path.Combine(webRoot, "images"));
app.UseStaticFiles();

if (Directory.Exists(glbDir))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(glbDir),
        RequestPath = "/models",
        OnPrepareResponse = ctx =>
        {
            var headers = ctx.Context.Response.Headers;
            headers["Cache-Control"] = "public, max-age=31536000, immutable";
        }
    });
}
app.UseMiddleware<honey_badger_api.Middleware.RequestLoggingMiddleware>();


// ─────────────────────────────────────────────────────────────────────────────
// 8) Pipeline
// ─────────────────────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("spa");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

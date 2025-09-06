// path: honey_badger_api/Program.cs
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
using System.Reflection.PortableExecutable;
using System.Text;

Env.Load(); // keep if present previously to load .env

var builder = WebApplication.CreateBuilder(args);

// 1) DB + Identity (unchanged)
builder.Services.AddDbContext<AppDbContext>(opt =>
{
    var cs = builder.Configuration.GetConnectionString("DefaultConnection")
             ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
    opt.UseMySql(cs, ServerVersion.AutoDetect(cs));
});

builder.Services.AddIdentity<AppUser, IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// 2) JWT auth (unchanged)
var jwtIssuer = builder.Configuration["Jwt:Issuer"]!;
var jwtAudience = builder.Configuration["Jwt:Audience"]!;
var jwtKey = builder.Configuration["Jwt:Key"]!;
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

builder.Services.AddAuthentication(o =>
{
    o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(o =>
{
    o.RequireHttpsMetadata = false; // set true behind HTTPS
    o.SaveToken = true;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = signingKey,
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization();

// 3) Email services (unchanged)
builder.Services.AddSingleton<IAppEmailSender, MailjetEmailSender>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender, IdentityEmailSenderAdapter>();

// 4) Controllers + Swagger (unchanged)
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
        { new OpenApiSecurityScheme{ Reference = new OpenApiReference{ Type = ReferenceType.SecurityScheme, Id = "Bearer"} }, Array.Empty<string>() }
    });
});

// 5) CORS
var corsOrigins = (builder.Configuration["Cors:Origins"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("spa", p =>
    {
        if (corsOrigins.Length > 0) p.WithOrigins(corsOrigins);
        else p.AllowAnyOrigin(); // dev fallback
        p.AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    });
});

var app = builder.Build();

// === Serve GLB models at /models (place this early) === 
var glbDir = builder.Configuration["ANIMATIONS_GLB_DIR"]
             ?? Path.Combine(app.Environment.ContentRootPath, "badger_animation_glb"); // default relative path 
app.Logger.LogInformation("GLB dir resolved to: {dir} (exists={exists})", glbDir, Directory.Exists(glbDir));
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
}  // === end /models mapping ===

// 7) Pipeline
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

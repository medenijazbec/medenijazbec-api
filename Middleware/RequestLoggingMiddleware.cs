using honey_badger_api.Data;
using honey_badger_api.Entities;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace honey_badger_api.Middleware
{
    public sealed class RequestLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestLoggingMiddleware> _log;

        private static readonly Regex Suspicious =
            new(@"(\.\./|\.env|wp-admin|phpmyadmin|select\s+.+\s+from|union\s+select|<script|%3Cscript|or\s+1=1)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> log)
        {
            _next = next;
            _log = log;
        }

        public async Task Invoke(HttpContext ctx, AppDbContext db)
        {
            var started = DateTime.UtcNow;

            // Quick ip-ban check
            var ip = GetIp(ctx);
            var now = DateTime.UtcNow;
            var activeBan = db.IpBans.FirstOrDefault(b => !b.Disabled &&
                                                          (b.ExpiresUtc == null || b.ExpiresUtc > now) &&
                                                          b.Kind == "ip" && b.Value == ip);
            if (activeBan != null)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("Forbidden");
                // persist the blocked request for audit
                db.RequestLogs.Add(new RequestLog
                {
                    StartedUtc = started,
                    DurationMs = 0,
                    Method = ctx.Request.Method,
                    Path = ctx.Request.Path,
                    Query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : null,
                    StatusCode = 403,
                    Ip = ip,
                    UserId = ctx.User?.Identity?.IsAuthenticated == true ? ctx.User.FindFirst("sub")?.Value : null,
                    Email = ctx.User?.Claims?.FirstOrDefault(c => c.Type.EndsWith("/email"))?.Value,
                    UserAgent = ctx.Request.Headers.UserAgent.ToString(),
                    Blocked = true,
                    BlockReason = "ip-ban",
                });
                await db.SaveChangesAsync();
                return;
            }

            var ua = ctx.Request.Headers.UserAgent.ToString();
            var path = ctx.Request.Path.ToString();
            var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : null;
            var wafHits = Suspicious.IsMatch(path + " " + query + " " + ua);

            // collect TLS if available
            var tls = ctx.Features.Get<ITlsHandshakeFeature>();
            var proto = ctx.Request.Protocol;

            // buffer length (optional)
            long? responseBytes = null;
            ctx.Response.OnStarting(() =>
            {
                if (ctx.Response.ContentLength.HasValue) responseBytes = ctx.Response.ContentLength.Value;
                return Task.CompletedTask;
            });

            try
            {
                await _next(ctx);
            }
            finally
            {
                var duration = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                var status = ctx.Response?.StatusCode ?? 0;

                var log = new RequestLog
                {
                    StartedUtc = started,
                    DurationMs = duration,
                    Method = ctx.Request.Method,
                    Path = path,
                    Query = query,
                    StatusCode = status,

                    UserId = ctx.User?.Identity?.IsAuthenticated == true ? ctx.User.FindFirst("sub")?.Value : null,
                    Email = ctx.User?.Claims?.FirstOrDefault(c => c.Type.EndsWith("/email"))?.Value,

                    Ip = ip,
                    UserAgent = ua,
                    UaFamily = UaFamily(ua),
                    IsBot = IsBot(ua),
                    Referrer = ctx.Request.Headers.Referer.ToString(),

                    Protocol = proto,
                    TlsProtocol = tls?.Protocol.ToString(),
                    TlsCipher = tls?.CipherAlgorithm.ToString(),

                    ResponseBytes = responseBytes,

                    Blocked = false,
                    WafFlagsJson = wafHits ? JsonSerializer.Serialize(new { hit = true, reason = "suspicious" }) : null
                };

                db.RequestLogs.Add(log);
                await db.SaveChangesAsync();
            }
        }

        private static string GetIp(HttpContext ctx)
        {
            var fwd = ctx.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrWhiteSpace(fwd))
                return fwd.Split(',')[0].Trim();
            var ip = ctx.Connection.RemoteIpAddress?.ToString();
            return string.IsNullOrWhiteSpace(ip) ? "0.0.0.0" : ip;
        }

        private static string UaFamily(string ua)
        {
            if (string.IsNullOrEmpty(ua)) return "unknown";
            if (ua.Contains("Chrome")) return "Chrome";
            if (ua.Contains("Firefox")) return "Firefox";
            if (ua.Contains("Safari") && !ua.Contains("Chrome")) return "Safari";
            if (ua.Contains("curl") || ua.Contains("Wget")) return "cli";
            return "other";
        }

        private static bool IsBot(string ua)
        {
            if (string.IsNullOrEmpty(ua)) return false;
            var u = ua.ToLowerInvariant();
            return u.Contains("bot") || u.Contains("crawler") || u.Contains("spider") || u.Contains("headless") || u.Contains("fetch");
        }
    }
}

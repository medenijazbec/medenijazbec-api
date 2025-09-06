using Mailjet.Client;
using Mailjet.Client.Resources;
using Newtonsoft.Json.Linq;
using honey_badger_api.Abstractions;

namespace honey_badger_api.Services;

public class MailjetEmailSender : IAppEmailSender
{
    private readonly IConfiguration _cfg;
    public MailjetEmailSender(IConfiguration cfg) => _cfg = cfg;

    public async Task SendAsync(string subject, string textBody, string? htmlBody = null,
                                string? toOverride = null, string? fromEmail = null, string? fromName = null)
    {
        var s = _cfg.GetSection("Mailjet");
        var apiKey = s["ApiKey"] ?? throw new InvalidOperationException("Mailjet:ApiKey missing");
        var apiSecret = s["ApiSecret"] ?? throw new InvalidOperationException("Mailjet:ApiSecret missing");
        var fromAddr = fromEmail ?? s["FromEmail"] ?? throw new InvalidOperationException("Mailjet:FromEmail missing");
        var fromNameOr = fromName ?? s["FromName"] ?? "Portfolio";
        var toAddr = toOverride ?? s["ToEmail"] ?? throw new InvalidOperationException("Mailjet:ToEmail missing");

        var client = new MailjetClient(apiKey, apiSecret);

        var req = new MailjetRequest { Resource = Send.Resource }
            .Property(Send.Messages, new JArray
            {
                new JObject
                {
                    ["From"]     = new JObject { ["Email"] = fromAddr, ["Name"] = fromNameOr },
                    ["To"]       = new JArray { new JObject { ["Email"] = toAddr } },
                    ["Subject"]  = subject,
                    ["TextPart"] = textBody,
                    ["HTMLPart"] = htmlBody ?? $"<pre>{System.Net.WebUtility.HtmlEncode(textBody)}</pre>"
                }
            });

        var resp = await client.PostAsync(req);

        if (!resp.IsSuccessStatusCode)
        {
            // Normalize anything (JObject/JToken/string/null) into strings
            var errObj = resp.GetErrorMessage();           // can be JObject/JToken/string depending on version
            string? errMsg = errObj?.ToString();

            // Some versions expose .Content as string; keep defensive
            string? contentMsg = resp.Content is null ? null : resp.Content.ToString();

            throw new InvalidOperationException(
                $"Mailjet send failed: {(int)resp.StatusCode} {resp.StatusCode} - {errMsg ?? contentMsg ?? "(no body)"}");
        }
    }
}

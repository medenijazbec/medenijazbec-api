using Microsoft.AspNetCore.Identity.UI.Services;
using honey_badger_api.Abstractions;

namespace honey_badger_api.Services;

public class IdentityEmailSenderAdapter : IEmailSender
{
    private readonly IAppEmailSender _inner;
    public IdentityEmailSenderAdapter(IAppEmailSender inner) => _inner = inner;

    public Task SendEmailAsync(string email, string subject, string htmlMessage)
        => _inner.SendAsync(subject, StripTags(htmlMessage), htmlMessage, toOverride: email);

    private static string StripTags(string html)
    {
        // super simple fallback
        return System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);
    }
}

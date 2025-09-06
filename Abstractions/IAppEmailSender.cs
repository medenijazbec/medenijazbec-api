namespace honey_badger_api.Abstractions;

public interface IAppEmailSender
{
    Task SendAsync(
        string subject,
        string textBody,
        string? htmlBody = null,
        string? toOverride = null,      // send to a specific address (e.g., customer)
        string? fromEmail = null,
        string? fromName = null);
}

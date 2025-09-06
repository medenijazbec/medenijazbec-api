namespace honey_badger_api.Interfaces
{
    public interface IEmailSender
    {
        Task SendAsync(string subject, string body);
    }
}

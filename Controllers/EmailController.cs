using honey_badger_api.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/email")]
public class EmailController : ControllerBase
{
    private readonly IAppEmailSender _email;
    public EmailController(IAppEmailSender email) => _email = email;

    [HttpPost("test")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Test([FromQuery] string to, [FromQuery] string subject, [FromQuery] string body)
    {
        await _email.SendAsync(subject, body, $"<pre>{System.Net.WebUtility.HtmlEncode(body)}</pre>", toOverride: to);
        return Ok(new { sent = true });
    }
}

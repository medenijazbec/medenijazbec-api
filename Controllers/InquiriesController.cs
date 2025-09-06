using honey_badger_api.Abstractions;
using honey_badger_api.Data;
using honey_badger_api.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/inquiries")]
public class InquiriesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IAppEmailSender _email;

    public InquiriesController(AppDbContext db, IAppEmailSender email)
    { _db = db; _email = email; }

    [AllowAnonymous]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] InquiryRequest req)
    {
        var entity = new ContactInquiry
        {
            Name = req.Name,
            Email = req.Email,
            Phone = req.Phone,
            Subject = req.Subject,
            Message = req.Message
        };
        _db.ContactInquiries.Add(entity);
        await _db.SaveChangesAsync();

        // Notify you
        var subj = $"New inquiry: {req.Subject ?? "(no subject)"}";
        var text = $"From: {req.Name} <{req.Email}>\nPhone: {req.Phone ?? "-"}\n\n{req.Message}";
        var html = $@"<p><b>From:</b> {System.Net.WebUtility.HtmlEncode(req.Name)} &lt;{System.Net.WebUtility.HtmlEncode(req.Email)}&gt;<br/>
            <b>Phone:</b> {System.Net.WebUtility.HtmlEncode(req.Phone ?? "-")}</p>
            <pre>{System.Net.WebUtility.HtmlEncode(req.Message)}</pre>";

        await _email.SendAsync(subj, text, html);

        // (Optional) auto-ack to the sender
        await _email.SendAsync("Thanks for reaching out",
            "Hvala za povpraševanje! Odgovorim kmalu.",
            "<p>Hvala za povpraševanje! Odgovorim kmalu.</p>",
            toOverride: req.Email);

        return Ok(new { entity.Id, status = entity.Status });
    }

    [Authorize(Roles = "Admin")]
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status)
    {
        var q = _db.ContactInquiries.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(i => i.Status == status);

        var list = await q
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(); // <-- needs using Microsoft.EntityFrameworkCore;

        return Ok(list);
    }
}

public record InquiryRequest(string? Name, string Email, string? Phone, string? Subject, string Message);

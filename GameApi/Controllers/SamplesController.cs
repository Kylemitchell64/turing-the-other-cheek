using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GameApi.Data;
using GameApi.Models;

namespace GameApi.Controllers;

// Writing-sample vault (paste-only). Feeds each user's style profile. The React
// Writing Samples screen reads/writes this: GET returns the list (its length drives
// the "AI knowledge of you" meter), POST adds one pasted sample, DELETE removes one.
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class SamplesController : ControllerBase
{
    // Paste-only cap per sample (matches the client textarea + the column max length).
    private const int MaxSampleChars = 10_000;

    private readonly GameContext _db;

    public SamplesController(GameContext db)
    {
        _db = db;
    }

    private string UserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("No user id on token");

    public record SampleDto(int Id, string Text, string Source, DateTime CreatedAt);
    public record AddSampleRequest(string Text);

    // GET /api/samples — this user's samples, newest first.
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var samples = await _db.WritingSamples
            .Where(s => s.UserId == UserId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new SampleDto(s.Id, s.Text, s.Source.ToString(), s.CreatedAt))
            .ToListAsync();

        return Ok(samples);
    }

    // POST /api/samples — add one pasted sample (Source=Upload), 10k char cap.
    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddSampleRequest req)
    {
        var text = (req?.Text ?? "").Trim();
        if (text.Length == 0)
            return BadRequest(new { error = "Sample can't be empty" });
        if (text.Length > MaxSampleChars)
            text = text[..MaxSampleChars];

        var sample = new WritingSample
        {
            UserId = UserId,
            Text = text,
            Source = SampleSource.Upload,
            CreatedAt = DateTime.UtcNow
        };
        _db.WritingSamples.Add(sample);
        await _db.SaveChangesAsync();

        // Enforce the per-tier rolling cap (guest 10 / user 200): drop the oldest beyond it.
        await SampleCaps.EnforceAsync(_db, UserId);

        return Ok(new SampleDto(sample.Id, sample.Text, sample.Source.ToString(), sample.CreatedAt));
    }

    // DELETE /api/samples/{id} — remove one of this user's samples.
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var sample = await _db.WritingSamples
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId);
        if (sample == null)
            return NotFound();

        _db.WritingSamples.Remove(sample);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

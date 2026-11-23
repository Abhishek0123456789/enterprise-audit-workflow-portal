using AuditPortal.API.Data;
using AuditPortal.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuditPortal.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AuditController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;

    public AuditController(AppDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    // GET: api/audit?page=1&pageSize=10&status=Pending&search=Finance
    [HttpGet]
    public async Task<IActionResult> GetAudits(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null)
    {
        var query = _context.AuditRecords.AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(a => a.Status == status);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(a => a.Title.Contains(search) || a.Department.Contains(search));

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { totalCount, page, pageSize, items });
    }

    // POST: api/audit (Only Auditor or Admin can create with PDF upload)
    [HttpPost]
    [Authorize(Roles = "Auditor,Admin")]
    public async Task<IActionResult> CreateAudit([FromForm] string title, [FromForm] string department, IFormFile? pdfFile)
    {
        string? savedFileName = null;

        if (pdfFile != null && pdfFile.Length > 0)
        {
            if (Path.GetExtension(pdfFile.FileName).ToLower() != ".pdf")
                return BadRequest(new { message = "Only PDF files are allowed." });

            var uploadsFolder = Path.Combine(_env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "uploads");
            Directory.CreateDirectory(uploadsFolder);

            savedFileName = $"{Guid.NewGuid()}_{pdfFile.FileName}";
            var filePath = Path.Combine(uploadsFolder, savedFileName);

            using var stream = new FileStream(filePath, FileMode.Create);
            await pdfFile.CopyToAsync(stream);
        }

        var record = new AuditRecord
        {
            Title = title,
            Department = department,
            Status = "Pending",
            PdfFileName = savedFileName,
            CreatedBy = User.Identity?.Name ?? "System"
        };

        _context.AuditRecords.Add(record);
        await _context.SaveChangesAsync();

        return Ok(record);
    }

    // PUT: api/audit/{id}/status (Only Reviewer or Admin can Approve/Reject)
    [HttpPut("{id}/status")]
    [Authorize(Roles = "Reviewer,Admin")]
    public async Task<IActionResult> UpdateStatus(int id, [FromQuery] string status)
    {
        var record = await _context.AuditRecords.FindAsync(id);
        if (record == null) return NotFound();

        record.Status = status; // Approved or Rejected
        await _context.SaveChangesAsync();

        return Ok(record);
    }

        // GET: api/audit/export
    [HttpGet("export")]
    public async Task<IActionResult> ExportCsv([FromQuery] string? status = null)
    {
        var query = _context.AuditRecords.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(a => a.Status == status);

        var records = await query.OrderByDescending(a => a.CreatedAt).ToListAsync();

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("ID,Title,Department,Status,CreatedBy,CreatedAt");

        foreach (var r in records)
        {
            csv.AppendLine($"{r.Id},\"{r.Title}\",\"{r.Department}\",{r.Status},{r.CreatedBy},{r.CreatedAt:yyyy-MM-dd}");
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"Audit_Report_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

}
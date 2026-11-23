using AuditPortal.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AuditPortal.API.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
}
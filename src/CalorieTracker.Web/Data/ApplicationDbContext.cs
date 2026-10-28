using CalorieTracker.Web.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CalorieTracker.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options), IDataProtectionKeyContext
{
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<NutritionGoalSnapshot> NutritionGoalSnapshots => Set<NutritionGoalSnapshot>();
    public DbSet<Food> Foods => Set<Food>();
    public DbSet<FoodDiaryEntry> FoodDiaryEntries => Set<FoodDiaryEntry>();
    public DbSet<FoodDiaryItem> FoodDiaryItems => Set<FoodDiaryItem>();
    public DbSet<WeightLog> WeightLogs => Set<WeightLog>();
    public DbSet<SavedMeal> SavedMeals => Set<SavedMeal>();
    public DbSet<SavedMealItem> SavedMealItems => Set<SavedMealItem>();
    public DbSet<FoodScan> FoodScans => Set<FoodScan>();
    // Auth cookie / antiforgery keys live in the database because the container filesystem is ephemeral.
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Every user-owned table cascades from the Identity user, so deleting an account deletes its data.
        b.Entity<UserProfile>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasOne(x => x.User).WithOne(u => u.Profile).HasForeignKey<UserProfile>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NutritionGoalSnapshot>(e =>
        {
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.EffectiveDate }).IsUnique();
        });
        b.Entity<Food>(e =>
        {
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.Name });
        });
        b.Entity<FoodDiaryEntry>(e =>
        {
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.DiaryDate });
            e.HasIndex(x => new { x.UserId, x.ClientRequestId }).IsUnique();
        });
        b.Entity<FoodDiaryItem>(e =>
        {
            e.HasOne(x => x.Entry).WithMany(x => x.Items).HasForeignKey(x => x.FoodDiaryEntryId).OnDelete(DeleteBehavior.Cascade);
            // Deleting a food keeps the logged snapshot.
            e.HasOne(x => x.Food).WithMany().HasForeignKey(x => x.FoodId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<WeightLog>(e =>
        {
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.Date }).IsUnique();
        });
        b.Entity<SavedMeal>(e =>
        {
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.SavedMealId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });
        b.Entity<SavedMealItem>().HasOne<Food>().WithMany().HasForeignKey(x => x.FoodId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<FoodScan>(e =>
        {
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FoodDiaryEntry>().WithMany().HasForeignKey(x => x.FoodDiaryEntryId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
        });

        // Nutrient and measurement columns: enough scale that rescaling servings doesn't accumulate rounding.
        foreach (var p in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
        {
            p.SetPrecision(12);
            p.SetScale(4);
        }
    }
}

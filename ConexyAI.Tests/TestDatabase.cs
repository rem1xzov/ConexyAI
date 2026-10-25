using ConexyAI.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

// TESTS: единая точка создания in-memory контекста. Набор создаёт десятки короткоживущих
// контекстов, у каждого свои options — EF начинает считать это ошибкой
// (ManyServiceProvidersCreatedWarning), и после двадцатого контекста падал бы каждый следующий тест.
// Предупреждение про производительность DI, а не про данные, поэтому здесь его гасим.
internal static class TestDatabase
{
    public static DbContextOptionsBuilder<DbConexy> UseTestDatabase(
        this DbContextOptionsBuilder<DbConexy> builder,
        string name,
        InMemoryDatabaseRoot? root = null) =>
        builder
            .UseInMemoryDatabase(name, root)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

    /// <summary>Перегрузка для <c>AddDbContext&lt;DbConexy&gt;((o) => ...)</c>: там приходит нетипизированный builder.</summary>
    public static DbContextOptionsBuilder UseTestDatabase(
        this DbContextOptionsBuilder builder,
        string name,
        InMemoryDatabaseRoot? root = null) =>
        builder
            .UseInMemoryDatabase(name, root)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
}

using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Server.C2Bridge;
using Server.Listeners;
using Server.Proxy;
using Server.Tasks;

namespace Server.Core;

public static class CrystalServices
{
    public static IServiceCollection AddCrystalServices(this IServiceCollection services)
    {
        services.AddDbContext<CrystalDb>(db =>
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = "CrystalC2.db",
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };

            db.UseSqlite(csb.ConnectionString);
        });

        services.AddMediatR(m =>
        {
            m.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());
        });

        services.AddScoped(typeof(IRepository<>), typeof(CrystalRepository<>));
        services.AddSingleton(typeof(IBroker<>),  typeof(CrystalBroker<>));
        
        services.AddSingleton<ListenerManager>();
        services.AddSingleton<TaskBroker>();
        services.AddSingleton<ProxyBroker>();
        services.AddSingleton<ProxyQueue>();
        services.AddScoped<C2Manager>();
        
        return services;
    }
}
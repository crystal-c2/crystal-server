using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Server.Auth;
using Server.Beacons;
using Server.Core;
using Server.Listeners;
using Server.Proxy;
using Server.Tasks;
using System.Text;

namespace Server;

public sealed class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = "CrystalC2",
                    ValidAudience = "CrystalC2",
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
                };
            });

        builder.Services.AddAuthorization();
        builder.Services.AddGrpc();
        builder.Services.AddCrystalServices();

        builder.Logging.AddFilter("LuckyPennySoftware.MediatR.License", LogLevel.None);

        var app = builder.Build();

        await using var scope = app.Services.CreateAsyncScope();
        await using var db = scope.ServiceProvider.GetRequiredService<CrystalDb>();

        var created = await db.Database.EnsureCreatedAsync();

        if (!created)
        {
            // start saved listeners
            var listeners = await db.Listeners.ToListAsync();
            var manager = scope.ServiceProvider.GetRequiredService<ListenerManager>();
            var factory = scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>();

            foreach (var listener in listeners)
            {
                if (listener.Status is not ListenerStatus.Running)
                    continue;

                var instance = new ListenerInstance(listener.Id, listener.BindPort, factory);

                try
                {
                    instance.Start();
                    manager.Add(listener.Id, instance);
                }
                catch
                {
                    // pokemon
                }
            }
        }

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGrpcService<AuthProtoService>();
        app.MapGrpcService<ListenerProtoService>();
        app.MapGrpcService<BeaconProtoService>();
        app.MapGrpcService<TasksProtoService>();
        app.MapGrpcService<ProxyProtoService>();
        
        app.Logger.LogInformation("Server started");

        await app.RunAsync();
    }
}
using AdamCodexHub.Codex;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Gateway;
using AdamCodexHub.Infrastructure.Database;
using AdamCodexHub.Infrastructure.Keys;
using AdamCodexHub.Infrastructure.Models;
using AdamCodexHub.Infrastructure.Paths;
using AdamCodexHub.Infrastructure.Providers;
using AdamCodexHub.Infrastructure.Security;
using AdamCodexHub.Providers;
using AdamCodexHub.Providers.Adapters;
using AdamCodexHub.Providers.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHttpClient();

        services.AddSingleton<AppPaths>();
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IKeyVault, DpapiKeyVault>();
        services.AddSingleton<IKeyPoolService, SqliteKeyPoolService>();
        services.AddSingleton<IProviderStore, SqliteProviderStore>();
        services.AddSingleton<IModelStore, SqliteModelStore>();

        services.AddSingleton<IProviderRegistryService, EmbeddedProviderRegistryService>();
        services.AddSingleton<IProviderManager, ProviderManager>();

        services.AddSingleton<OpenAiCompatibleAdapter>();
        services.AddSingleton<IProviderAdapter>(sp =>
            sp.GetRequiredService<OpenAiCompatibleAdapter>());
        services.AddSingleton<IProviderAdapter, OpenAiResponsesAdapter>();
        services.AddSingleton<IModelDiscoveryService, ModelDiscoveryService>();
        services.AddSingleton<ICompatibilityService, CompatibilityService>();
        services.AddSingleton<IKeyTestService, KeyTestService>();

        services.AddSingleton<ICodexConfigService, CodexConfigService>();
        services.AddSingleton<IProjectStateService, FileProjectStateService>();
        services.AddSingleton<ISessionContinuityService, SessionContinuityService>();
        services.AddSingleton<IProviderActivationService, ProviderActivationService>();
        services.AddSingleton<IGatewayService, LocalGatewayService>();
    })
    .Build();

await host.StartAsync();

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";

switch (command)
{
    case "providers":
    {
        var providers = host.Services.GetRequiredService<IProviderManager>();
        await providers.InitializeAsync();

        foreach (var provider in await providers.GetAllAsync())
        {
            Console.WriteLine($"{provider.Id,-24} {provider.Name}");
        }

        break;
    }

    case "refresh":
    {
        var path = args.Skip(1).FirstOrDefault() ?? Environment.CurrentDirectory;
        var service = host.Services.GetRequiredService<IProjectStateService>();
        var state = await service.RefreshAsync(path, SyncLevel.Normal);

        Console.WriteLine($"Project revision: {state.Revision}");
        Console.WriteLine($"Changed files: {state.ChangedFiles.Count}");
        break;
    }

    case "gateway":
    {
        var gateway = host.Services.GetRequiredService<IGatewayService>();
        await gateway.StartAsync();

        Console.WriteLine($"Gateway running at http://127.0.0.1:{gateway.Port}");
        Console.WriteLine("Press Ctrl+C to exit.");
        await Task.Delay(Timeout.Infinite);
        break;
    }

    default:
    {
        var codex = host.Services.GetRequiredService<ICodexConfigService>();
        Console.WriteLine("Adam CodexHub");
        Console.WriteLine($"Codex home: {codex.CodexHome}");
        Console.WriteLine($"Account profile: {(await codex.HasAccountProfileAsync() ? "found" : "not found")}");
        Console.WriteLine();
        Console.WriteLine("Commands: status | providers | refresh [project] | gateway");
        break;
    }
}

await host.StopAsync();

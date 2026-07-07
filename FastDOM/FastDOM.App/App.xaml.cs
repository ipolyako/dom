using System.IO;
using System.Windows;
using FastDOM.App.Services;
using FastDOM.App.ViewModels;
using FastDOM.App.Views;
using FastDOM.Broker;
using FastDOM.Broker.Interfaces;
using FastDOM.Broker.Mock;
using FastDOM.Broker.Schwab.Auth;
using FastDOM.Infrastructure.Config;
using FastDOM.Broker.Schwab.Client;
using FastDOM.Broker.Schwab.Mapping;
using FastDOM.Core.Enums;
using FastDOM.Infrastructure.Config;
using FastDOM.Infrastructure.Logging;
using FastDOM.Infrastructure.Security;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Mock;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace FastDOM.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ConfigureLogging();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Fatal(ex.Exception, "Unhandled UI exception");
            System.Windows.MessageBox.Show(
                $"Unhandled error:\n\n{ex.Exception.GetType().Name}: {ex.Exception.Message}\n\n{ex.Exception.StackTrace}",
                "FastDOM Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            var error = ex.ExceptionObject as Exception;
            Log.Fatal(error, "Unhandled AppDomain exception (terminating={Term})", ex.IsTerminating);
            if (ex.IsTerminating)
                System.Windows.MessageBox.Show(
                    $"Fatal error (app will close):\n\n{error?.GetType().Name}: {error?.Message}\n\n{error?.StackTrace}",
                    "FastDOM Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        _services = BuildServices();
        var window = _services.GetRequiredService<MainWindow>();
        window.Show();
    }

    private static void ConfigureLogging()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FastDOM", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "fastdom-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDir, "errors-.log"),
                restrictedToMinimumLevel: LogEventLevel.Warning,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10)
            .CreateLogger();
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddSerilog(dispose: true));

        // Config
        services.AddSingleton<ConfigManager>(sp =>
        {
            var cm = new ConfigManager(sp.GetRequiredService<ILogger<ConfigManager>>());
            cm.LoadAll();
            return cm;
        });

        // Infrastructure
        services.AddSingleton<SecureStorage>();
        services.AddSingleton<AuditLogger>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigManager>();
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FastDOM", "logs");
            return new AuditLogger(sp.GetRequiredService<ILogger<AuditLogger>>(), logDir);
        });

        // Risk
        services.AddSingleton<RiskManager>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigManager>();
            return new RiskManager(
                sp.GetRequiredService<ILogger<RiskManager>>(),
                cfg.RiskProfile);
        });
        services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());

        // Broker & market data — wired based on mode
        services.AddSingleton<IBrokerClient>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigManager>();
            return cfg.AppSettings.Mode == TradingMode.Simulation
                ? (IBrokerClient)new MockBrokerClient(sp.GetRequiredService<ILogger<MockBrokerClient>>())
                : CreateSchwabBroker(sp);
        });

        services.AddSingleton<IMarketDataClient>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigManager>();
            return cfg.AppSettings.Mode == TradingMode.Simulation
                ? (IMarketDataClient)new MockMarketDataClient(sp.GetRequiredService<ILogger<MockMarketDataClient>>())
                : CreateSchwabMarketData(sp);
        });

        // HotkeyConfig must be registered so HotkeyService can receive it via DI
        services.AddSingleton(sp => sp.GetRequiredService<ConfigManager>().HotkeyConfig);

        // App services
        services.AddSingleton<OrderService>();
        services.AddSingleton<DomService>();
        services.AddSingleton<HotkeyService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<DomViewModel>();
        services.AddTransient<PositionViewModel>();
        services.AddTransient<HotButtonsViewModel>();
        services.AddTransient<OrderTicketViewModel>();

        // Views
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }

    private static IBrokerClient CreateSchwabBroker(IServiceProvider sp)
    {
        var cfg    = sp.GetRequiredService<ConfigManager>();
        var derby  = new DerbyTokenProvider(
            sp.GetRequiredService<ILogger<DerbyTokenProvider>>(),
            cfg.TokenSource);
        var auth   = new SchwabAuthProvider(
            sp.GetRequiredService<ILogger<SchwabAuthProvider>>(),
            cfg.SchwabConfig,
            derby);
        return new SchwabBrokerClient(
            sp.GetRequiredService<ILogger<SchwabBrokerClient>>(),
            cfg.SchwabConfig, auth,
            new SchwabOrderMapper(sp.GetRequiredService<ILogger<SchwabOrderMapper>>()));
    }

    private static IMarketDataClient CreateSchwabMarketData(IServiceProvider sp)
    {
        var cfg   = sp.GetRequiredService<ConfigManager>();
        var derby = new DerbyTokenProvider(
            sp.GetRequiredService<ILogger<DerbyTokenProvider>>(),
            cfg.TokenSource);
        var auth  = new SchwabAuthProvider(
            sp.GetRequiredService<ILogger<SchwabAuthProvider>>(),
            cfg.SchwabConfig,
            derby);
        return new SchwabMarketDataClient(
            sp.GetRequiredService<ILogger<SchwabMarketDataClient>>(),
            cfg.SchwabConfig, auth);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

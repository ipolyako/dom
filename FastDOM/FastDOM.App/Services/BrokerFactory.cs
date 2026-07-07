using FastDOM.Broker.Alpaca.Client;
using FastDOM.Broker.Interfaces;
using FastDOM.Broker.Mock;
using FastDOM.Broker.Proxies;
using FastDOM.Broker.Schwab.Auth;
using FastDOM.Broker.Schwab.Client;
using FastDOM.Broker.Schwab.Mapping;
using FastDOM.Core.Enums;
using FastDOM.Infrastructure.Config;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Mock;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.Services;

public class BrokerFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConfigManager _config;
    private readonly RuntimeBrokerProxy _brokerProxy;
    private readonly RuntimeMarketDataProxy _marketDataProxy;

    public BrokerFactory(
        ILoggerFactory loggerFactory,
        ConfigManager config,
        RuntimeBrokerProxy brokerProxy,
        RuntimeMarketDataProxy marketDataProxy)
    {
        _loggerFactory    = loggerFactory;
        _config           = config;
        _brokerProxy      = brokerProxy;
        _marketDataProxy  = marketDataProxy;
    }

    public async Task SwitchModeAsync(TradingMode mode, CancellationToken ct = default)
    {
        var (broker, md) = Create(mode);
        await _brokerProxy.SwapAsync(broker, ct);
        await _marketDataProxy.SwapAsync(md, ct);
    }

    public (IBrokerClient broker, IMarketDataClient md) Create(TradingMode mode) => mode switch
    {
        TradingMode.SchwabLive   => CreateSchwab(),
        TradingMode.AlpacaPaper  => CreateAlpaca(isPaper: true),
        TradingMode.AlpacaLive   => CreateAlpaca(isPaper: false),
        _                        => CreateSim(),
    };

    private (IBrokerClient, IMarketDataClient) CreateSim()
    {
        var broker = new MockBrokerClient(_loggerFactory.CreateLogger<MockBrokerClient>());
        var md     = new MockMarketDataClient(_loggerFactory.CreateLogger<MockMarketDataClient>());
        return (broker, md);
    }

    private (IBrokerClient, IMarketDataClient) CreateSchwab()
    {
        var cfg    = _config.SchwabConfig;
        var ts     = _config.TokenSource;
        var derby  = new DerbyTokenProvider(_loggerFactory.CreateLogger<DerbyTokenProvider>(), ts);
        var auth   = new SchwabAuthProvider(_loggerFactory.CreateLogger<SchwabAuthProvider>(), cfg, derby);
        var mapper = new SchwabOrderMapper(_loggerFactory.CreateLogger<SchwabOrderMapper>());
        var broker = new SchwabBrokerClient(_loggerFactory.CreateLogger<SchwabBrokerClient>(), cfg, auth, mapper);
        var md     = new SchwabMarketDataClient(_loggerFactory.CreateLogger<SchwabMarketDataClient>(), cfg, auth);
        return (broker, md);
    }

    private (IBrokerClient, IMarketDataClient) CreateAlpaca(bool isPaper)
    {
        var src = _config.AlpacaConfig;
        var cfg = new AlpacaConfig { ApiKey = src.ApiKey, ApiSecret = src.ApiSecret, IsPaper = isPaper };
        var broker = new AlpacaBrokerClient(_loggerFactory.CreateLogger<AlpacaBrokerClient>(), cfg);
        var md     = new AlpacaMarketDataClient(_loggerFactory.CreateLogger<AlpacaMarketDataClient>(), cfg);
        return (broker, md);
    }
}

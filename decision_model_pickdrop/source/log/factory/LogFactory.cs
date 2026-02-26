using LogLib.log.strategy;

namespace LogLib.log.strategy.factory;

public class LogFactory : ILogStrategyFactory
{
    private Dictionary<StrategyKind, ILogOutputStrategy> _strategies = new Dictionary<StrategyKind, ILogOutputStrategy>();

    public ILogOutputStrategy Create(CreateContext ctx)
    {
        if (_strategies.TryGetValue(ctx.Kind, out var strategy))
        {
            return strategy;
        }

        switch (ctx.Kind)
        {
            case StrategyKind.Console:
                strategy = new ConsoleOutputStrategy();
                break;

            case StrategyKind.FileDaily:
            case StrategyKind.FileHourly:
            case StrategyKind.Zmq:
            default:
                throw new NotSupportedException($"Unsupported strategy: {ctx.Kind}");
        }

        _strategies.Add(ctx.Kind, strategy);
        return strategy;
    }
}
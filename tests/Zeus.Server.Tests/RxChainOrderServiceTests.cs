using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class RxChainOrderServiceTests
{
    [Fact]
    public void NewRxPlugin_AttachesParkedUntilOperatorActivates()
    {
        using var store = Store();
        var svc = new RxChainOrderService(store, NullLogger<RxChainOrderService>.Instance);
        var orderChanged = 0;
        svc.OrderChanged += _ => orderChanged++;

        svc.OnPluginAttached("com.openhpsdr.zeus.rxvst.clear");

        Assert.Empty(svc.CurrentOrder);
        Assert.Contains("com.openhpsdr.zeus.rxvst.clear", svc.ParkedForTest);
        Assert.Equal(0, orderChanged);

        Assert.True(svc.TrySetParked("com.openhpsdr.zeus.rxvst.clear", parked: false, out var error));
        Assert.Null(error);
        Assert.Equal(["com.openhpsdr.zeus.rxvst.clear"], svc.CurrentOrder);
        Assert.Equal(1, orderChanged);
    }

    [Fact]
    public void ActiveRxOrder_ReordersOnlyActiveRxPlugins()
    {
        using var store = Store();
        var svc = new RxChainOrderService(store, NullLogger<RxChainOrderService>.Instance);
        svc.OnPluginAttached("com.openhpsdr.zeus.rxvst.clear");
        svc.OnPluginAttached("com.openhpsdr.zeus.rxvst.rnnoise");
        svc.TrySetParked("com.openhpsdr.zeus.rxvst.clear", parked: false, out _);
        svc.TrySetParked("com.openhpsdr.zeus.rxvst.rnnoise", parked: false, out _);

        Assert.True(svc.TrySetOrder(
            ["com.openhpsdr.zeus.rxvst.rnnoise", "com.openhpsdr.zeus.rxvst.clear"],
            out var error));

        Assert.Null(error);
        Assert.Equal(
            ["com.openhpsdr.zeus.rxvst.rnnoise", "com.openhpsdr.zeus.rxvst.clear"],
            svc.CurrentOrder);
    }

    private static RxChainOrderStore Store()
    {
        var path = Path.Combine(Path.GetTempPath(), "zeus-rx-chain-" + Guid.NewGuid().ToString("N") + ".db");
        return new RxChainOrderStore(NullLogger<RxChainOrderStore>.Instance, path);
    }
}

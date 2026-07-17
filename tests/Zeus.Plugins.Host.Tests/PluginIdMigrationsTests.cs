// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugins.Contracts.Registry;
using Zeus.Plugins.Host.Registry;

namespace Zeus.Plugins.Host.Tests;

public sealed class PluginIdMigrationsTests
{
    [Fact]
    public void RegistryBrowseFilter_HidesSupersededOldIds()
    {
        var catalog = new RegistryCatalog
        {
            Plugins = new[]
            {
                Entry("com.kb2uka.voyeur"),
                Entry("com.kb2uka.recorder"),
                Entry("com.kb2uka.digital"),
                Entry("org.openhpsdr.digital"),
                Entry("org.openhpsdr.other"),
            },
        };

        var filtered = PluginIdMigrations.FilterSupersededEntries(catalog);

        Assert.DoesNotContain(filtered.Plugins, p => PluginIdMigrations.Map.ContainsKey(p.Id));
        Assert.Contains(filtered.Plugins, p => p.Id == "org.openhpsdr.digital");
        Assert.Contains(filtered.Plugins, p => p.Id == "org.openhpsdr.other");
    }

    private static PluginEntry Entry(string id) => new()
    {
        Id = id,
        Name = id,
    };
}

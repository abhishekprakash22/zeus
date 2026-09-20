// SPDX-License-Identifier: GPL-2.0-or-later
//
// Publish-on-log to QRZ.com: the gate and the stored preference. Uploading a
// QSO the operator did not ask to upload is the failure that matters here, so
// the gate is tested on its own rather than through the network client.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class QrzAutoPublishTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-qrzpub-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Theory]
    // Opt-in off: nothing is uploaded, API key or not.
    [InlineData(false, false, "Off")]
    [InlineData(false, true, "Off")]
    // Opted in but no key: nothing to upload with — and we say so in the log.
    [InlineData(true, false, "NoApiKey")]
    // Both present: publish.
    [InlineData(true, true, "Publish")]
    public void BothTheOptInAndAnApiKeyAreRequired(
        bool enabled, bool hasApiKey, string expected)
    {
        Assert.Equal(expected, QrzAutoPublishService.Decide(enabled, hasApiKey).ToString());
    }

    [Fact]
    public void ThePreferenceDefaultsOff_AndPersists()
    {
        using (var store = new QrzPublishSettingsStore(
            NullLogger<QrzPublishSettingsStore>.Instance, _dbPath))
        {
            Assert.False(store.Get().AutoPublishOnLog);       // never upload uninvited
            Assert.True(store.Set(new QrzPublishSettings(true)).AutoPublishOnLog);
        }

        using var reopened = new QrzPublishSettingsStore(
            NullLogger<QrzPublishSettingsStore>.Instance, _dbPath);
        Assert.True(reopened.Get().AutoPublishOnLog);
    }
}

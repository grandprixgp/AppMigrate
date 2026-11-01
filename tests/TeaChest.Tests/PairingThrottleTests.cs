using System.Net;
using TeaChest.Models;
using TeaChest.Services.Lan;

namespace TeaChest.Tests;

public class PairingThrottleTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static readonly IPAddress Peer = IPAddress.Parse("192.168.1.20");

    private static PairingThrottle NewThrottle(FakeClock clock) => new(clock.Read);

    [Fact]
    public void AnAddressWithNoHistoryCanAsk()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        Assert.True(throttle.TryBegin(Peer));
    }

    [Fact]
    public void ADeclineBlocksTheAddressForThirtySeconds()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        throttle.Declined(Peer);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(throttle.TryBegin(Peer));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryBegin(Peer));
    }

    [Fact]
    public void ASecondDeclineStillOnlyCostsThirtySeconds()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(throttle.TryBegin(Peer));
        throttle.Declined(Peer);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(throttle.TryBegin(Peer));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryBegin(Peer));
    }

    [Fact]
    public void AThirdDeclineWithinTenMinutesCostsFiveMinutes()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromSeconds(31));
        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromSeconds(31));
        throttle.Declined(Peer);

        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.False(throttle.TryBegin(Peer));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryBegin(Peer));
    }

    [Fact]
    public void DeclinesSpreadPastTenMinutesDoNotEscalate()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromMinutes(6));
        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromMinutes(6));
        // The first decline is now outside the window, so this is only the second inside it.
        throttle.Declined(Peer);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(throttle.TryBegin(Peer));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryBegin(Peer));
    }

    [Fact]
    public void AnAcceptedPairingClearsTheHistory()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromSeconds(31));
        throttle.Declined(Peer);
        throttle.Accepted(Peer);

        // If the two earlier declines had survived, the next one would already cost five minutes.
        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(throttle.TryBegin(Peer));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryBegin(Peer));
    }

    [Fact]
    public void MappedAndPlainAddressesShareOneCooldown()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        throttle.Declined(IPAddress.Parse("::ffff:10.0.0.1"));

        Assert.False(throttle.TryBegin(IPAddress.Parse("10.0.0.1")));
        Assert.False(throttle.TryBegin(IPAddress.Parse("::ffff:10.0.0.1")));

        throttle.Declined(IPAddress.Parse("10.0.0.1"));
        throttle.Declined(IPAddress.Parse("::ffff:10.0.0.1"));

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(throttle.TryBegin(IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void TheOldestAddressIsDroppedPastTheCap()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);
        var oldest = IPAddress.Parse("10.0.0.1");

        throttle.Declined(oldest);
        throttle.Declined(oldest);
        for (int i = 0; i < 256; i++)
        {
            throttle.Declined(new IPAddress(new byte[] { 10, 10, 0, (byte)i }));
        }

        // Dropped at the cap, so this decline starts over at thirty seconds. If its history had
        // survived it would be the third decline and the address would wait five minutes.
        throttle.Declined(oldest);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(throttle.TryBegin(oldest));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryBegin(oldest));
    }
}

public class PairingThrottleSessionTests : IAsyncLifetime
{
    private sealed class TinyInventory : IInventoryProvider
    {
        public int AppCount => 1;
        public Task<ManifestDocument> GetInventoryAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ManifestDocument
            {
                Apps = new List<AppRecord> { new() { Name = "Git", Id = "Git.Git", InstallType = "winget", Source = "winget" } }
            });
    }

    private readonly DeviceIdentity _serverIdentity = DeviceIdentity.CreateEphemeral("OLD-PC");
    private readonly DeviceIdentity _clientIdentity = DeviceIdentity.CreateEphemeral("NEW-PC");
    private readonly KnownPeers _serverPeers = KnownPeers.InMemory();
    private readonly KnownPeers _clientPeers = KnownPeers.InMemory();
    private int _confirmCalls;
    private PeerServer _server = null!;

    public Task InitializeAsync()
    {
        _server = new PeerServer(_serverIdentity, _serverPeers, new TinyInventory(), (_, _) =>
        {
            Interlocked.Increment(ref _confirmCalls);
            return Task.FromResult(false);
        });
        _server.Start(0);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _serverIdentity.Dispose();
        _clientIdentity.Dispose();
    }

    private Task<InventoryResult> FetchAsync()
        => new PeerClient(_clientIdentity, _clientPeers)
            .FetchInventoryAsync(new IPEndPoint(IPAddress.Loopback, _server.Port), 0, null, CancellationToken.None);

    [Fact]
    public async Task ADeclinedClientCannotPromptAgainStraightAway()
    {
        var throttled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Activity += line =>
        {
            if (line.Contains("too many attempts", StringComparison.OrdinalIgnoreCase)) throttled.TrySetResult(line);
        };

        await Assert.ThrowsAsync<PeerRejectedException>(() => FetchAsync());
        Assert.Equal(1, _confirmCalls);

        var second = await Assert.ThrowsAsync<PeerRejectedException>(() => FetchAsync());
        Assert.Equal("Too many pairing attempts. Wait a moment and try again.", second.Message);
        Assert.Equal(1, _confirmCalls);

        await throttled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

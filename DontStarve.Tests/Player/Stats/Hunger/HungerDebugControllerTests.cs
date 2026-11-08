using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Hunger;

public sealed class HungerDebugControllerTests
{
    private const string MasterPlayerKey = "1";

    [Fact]
    public void AbsoluteAndPercentageSetUseHostSnapshotAndReportActualValue()
    {
        var service = new FakeHungerDebugBackend();
        var controller = new HungerDebugController(service, () => true);

        var absolute = controller.SetAbsolute(MasterPlayerKey, 80d);
        var percentage = controller.SetPercentage(MasterPlayerKey, 50d);

        Assert.Equal(HungerDebugResultStatus.Applied, absolute.Status);
        Assert.Equal(80d, absolute.Current);
        Assert.Equal(HungerDebugResultStatus.Applied, percentage.Status);
        Assert.Equal(75d, percentage.Current);
    }

    [Fact]
    public void DisabledSystemDoesNotModifyHunger()
    {
        var service = new FakeHungerDebugBackend();
        var controller = new HungerDebugController(service, () => false);

        var result = controller.SetAbsolute(MasterPlayerKey, 20d);

        Assert.Equal(HungerDebugResultStatus.Disabled, result.Status);
        Assert.Equal(150d, service.Snapshot.Current);
    }

    [Fact]
    public void LockBlocksOrdinaryChangesButDebugSetStillWorks()
    {
        var service = new FakeHungerDebugBackend();
        var controller = new HungerDebugController(service, () => true);

        Assert.Equal(HungerDebugResultStatus.Applied, controller.SetLocked(true).Status);
        var ordinary = service.SetOrdinaryValue(MasterPlayerKey, 20d);
        var debug = controller.SetAbsolute(MasterPlayerKey, 30d);

        Assert.False(ordinary.Accepted);
        Assert.Equal("hunger-change-locked-by-debug-command", ordinary.Reason);
        Assert.Equal(HungerDebugResultStatus.Applied, debug.Status);
        Assert.Equal(30d, debug.Current);
    }

    [Fact]
    public void ClientSessionCannotUseDebugSet()
    {
        var service = new FakeHungerDebugBackend { Role = HungerAuthorityRole.Client };
        var controller = new HungerDebugController(service, () => true);

        var result = controller.SetAbsolute("2", 20d);

        Assert.Equal(HungerDebugResultStatus.HostOnly, result.Status);
    }

    [Fact]
    public void SettingTheCurrentValueReportsNoChange()
    {
        var service = new FakeHungerDebugBackend();
        var controller = new HungerDebugController(service, () => true);

        var result = controller.SetAbsolute(MasterPlayerKey, 150d);

        Assert.Equal(HungerDebugResultStatus.NoChange, result.Status);
        Assert.Equal(150d, result.Current);
    }

    private sealed class FakeHungerDebugBackend : IHungerDebugBackend
    {
        internal HungerAuthorityRole Role { get; set; } = HungerAuthorityRole.Host;

        internal bool IsDebugLocked { get; private set; }

        internal HungerPlayerSnapshot Snapshot { get; } = new()
        {
            PlayerKey = MasterPlayerKey,
            Current = 150d,
            Maximum = 150d,
            Revision = 0,
        };

        HungerAuthorityRole IHungerDebugBackend.Role => Role;

        bool IHungerDebugBackend.IsDebugLocked => IsDebugLocked;

        bool IHungerDebugBackend.TryGetSnapshot(
            string playerKey,
            out HungerPlayerSnapshot snapshot
        )
        {
            snapshot = Snapshot.Clone();
            return Role == HungerAuthorityRole.Host && playerKey == Snapshot.PlayerKey;
        }

        bool IHungerDebugBackend.TryEnsureHostPlayer(
            string playerKey,
            out HungerPlayerSnapshot snapshot,
            out string reason
        )
        {
            snapshot = Snapshot.Clone();
            reason = Role == HungerAuthorityRole.Host
                ? "debug-test-player-ready"
                : "host-hunger-authority-session-is-not-active";
            return Role == HungerAuthorityRole.Host && playerKey == Snapshot.PlayerKey;
        }

        HungerDebugMutationResult IHungerDebugBackend.SetDebugValue(
            string playerKey,
            double value
        )
        {
            if (Role != HungerAuthorityRole.Host)
                return new(false, false, "host-hunger-authority-session-is-not-active", null);
            var bounded = Math.Clamp(value, 0d, Snapshot.Maximum);
            var changed = !bounded.Equals(Snapshot.Current);
            Snapshot.Current = bounded;
            if (changed)
                Snapshot.Revision++;
            return new(true, changed, changed ? "hunger-value-changed" : "hunger-value-is-unchanged", Snapshot.Clone());
        }

        void IHungerDebugBackend.SetDebugLocked(bool locked)
        {
            IsDebugLocked = locked;
        }

        internal HungerDebugMutationResult SetOrdinaryValue(string playerKey, double value)
        {
            if (IsDebugLocked)
                return new(false, false, "hunger-change-locked-by-debug-command", Snapshot.Clone());
            return ((IHungerDebugBackend)this).SetDebugValue(playerKey, value);
        }
    }
}

using TypeSwitcher.Interop;
using Xunit;

namespace TypeSwitcher.Tests;

public sealed class FocusTrackingLifetimeTests
{
    [Fact]
    public void DisposeDuringRegistrationRemovesLateSubscription()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var removed = new ManualResetEventSlim();
        var manager = new ExclusionManager
        {
            AddFocusHandler = _ => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); },
            RemoveFocusHandler = _ => removed.Set()
        };
        try
        {
            manager.StartFocusTracking();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            manager.Dispose();
            release.Set();
            Assert.True(removed.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(manager.FocusTrackingActive);
        }
        finally { release.Set(); manager.Dispose(); }
    }

    [Fact]
    public void FailedRegistrationCanBeRetried()
    {
        int attempts = 0, removals = 0;
        var manager = new ExclusionManager
        {
            AddFocusHandler = _ => { if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("transient registration failure"); },
            RemoveFocusHandler = _ => Interlocked.Increment(ref removals)
        };
        try
        {
            manager.StartFocusTracking();
            Assert.True(SpinWait.SpinUntil(() => manager.FocusTrackingError != null, TimeSpan.FromSeconds(5)));
            Assert.False(manager.FocusTrackingActive);
            manager.StartFocusTracking();
            Assert.True(SpinWait.SpinUntil(() => manager.FocusTrackingActive, TimeSpan.FromSeconds(5)));
            Assert.Null(manager.FocusTrackingError);
            Assert.Equal(2, Volatile.Read(ref attempts));
        }
        finally { manager.Dispose(); }
        Assert.Equal(1, Volatile.Read(ref removals));
    }

    [Fact]
    public void DisposedManagerCannotRegisterAgain()
    {
        int registrations = 0;
        var manager = new ExclusionManager { AddFocusHandler = _ => Interlocked.Increment(ref registrations) };
        manager.Dispose();
        manager.StartFocusTracking();
        Assert.False(manager.FocusTrackingActive);
        Assert.Equal(0, Volatile.Read(ref registrations));
    }
}

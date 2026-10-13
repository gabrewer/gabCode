using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WatcherInvalidationTests
{
    [Fact]
    public void Burst_of_callbacks_occupies_only_one_pending_slot_until_consumed()
    {
        var gate = new WatcherInvalidation();
        Parallel.For(0, 100_000, _ => gate.Signal());
        Assert.True(gate.Consume());
        Assert.False(gate.Consume());
        gate.Signal();
        Assert.True(gate.Consume());
    }

    [Fact]
    public void Reset_atomically_discards_old_signals_even_when_callbacks_race_with_it()
    {
        var gate = new WatcherInvalidation();
        for (var round = 0; round < 100; ++round)
        {
            var previous = gate.Generation;
            Parallel.Invoke(gate.Reset, () => Parallel.For(0, 1_000, _ => gate.Signal(previous)));
            Assert.False(gate.Consume());
        }
    }

    [Fact]
    public void Old_watcher_signals_cannot_invalidate_a_new_selection()
    {
        var gate = new WatcherInvalidation();
        var previous = gate.Generation;
        gate.Signal(previous);
        gate.Reset();
        gate.Signal(previous);
        Assert.False(gate.Consume());
        gate.Signal(gate.Generation);
        Assert.True(gate.Consume());
    }
}

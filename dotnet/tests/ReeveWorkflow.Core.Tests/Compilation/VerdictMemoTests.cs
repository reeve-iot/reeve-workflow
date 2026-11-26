using FluentAssertions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class VerdictMemoTests
{
    [Fact]
    public void A_replace_over_the_entry_it_read_installs()
    {
        var memo = new VerdictMemo(capacity: 8);
        var id = Guid.NewGuid();
        var first = Entry();
        var second = Entry();

        memo.Replace(id, observed: null, first).Should().BeTrue();
        memo.Replace(id, observed: first, second).Should().BeTrue();

        memo.Read(id).Should().BeSameAs(second);
    }

    [Fact]
    public void A_replace_whose_entry_was_replaced_meanwhile_is_dropped()
    {
        var memo = new VerdictMemo(capacity: 8);
        var id = Guid.NewGuid();
        var read = Entry();
        var newer = Entry();
        memo.Replace(id, null, read);
        memo.Replace(id, read, newer);

        memo.Replace(id, observed: read, Entry()).Should().BeFalse();

        memo.Read(id).Should().BeSameAs(newer);
    }

    [Fact]
    public void A_replace_that_read_an_empty_slot_is_dropped_once_another_writer_installed()
    {
        var memo = new VerdictMemo(capacity: 8);
        var id = Guid.NewGuid();
        var winner = Entry();
        memo.Replace(id, null, winner);

        memo.Replace(id, observed: null, Entry()).Should().BeFalse();

        memo.Read(id).Should().BeSameAs(winner);
    }

    [Fact]
    public void A_replace_that_read_an_entry_is_dropped_once_that_entry_is_gone()
    {
        var memo = new VerdictMemo(capacity: 8);
        var id = Guid.NewGuid();
        var read = Entry();

        memo.Replace(id, observed: read, Entry()).Should().BeFalse();

        memo.Read(id).Should().BeNull();
    }

    [Fact]
    public void An_entry_equal_in_content_to_the_one_read_is_still_a_different_entry()
    {
        var memo = new VerdictMemo(capacity: 8);
        var id = Guid.NewGuid();
        var verdict = Verdict();
        var installed = new MemoEntry("d", "h", verdict, saved: false);
        memo.Replace(id, null, installed);

        memo.Replace(id, observed: new MemoEntry("d", "h", verdict, saved: false), Entry()).Should().BeFalse();

        memo.Read(id).Should().BeSameAs(installed);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void A_capacity_crossing_evicts_half_and_keeps_the_entry_it_just_installed(int capacity)
    {
        var memo = new VerdictMemo(capacity);
        var ids = Enumerable.Range(0, capacity + 1).Select(_ => Guid.NewGuid()).ToArray();

        foreach (var id in ids[..^1])
            memo.Replace(id, null, Entry());
        memo.Count.Should().Be(capacity, "nothing is evicted until the count exceeds capacity");

        var last = Entry();
        memo.Replace(ids[^1], null, last).Should().BeTrue();

        memo.Count.Should().Be(capacity + 1 - (capacity + 1) / 2);
        memo.Read(ids[^1]).Should().BeSameAs(last);
    }

    [Fact]
    public void A_capacity_crossing_evicts_the_entry_installed_longest_ago_first()
    {
        var memo = new VerdictMemo(capacity: 2);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
            memo.Replace(id, null, Entry());

        ids.Select(id => memo.Read(id) is not null).Should().Equal(
            [false, false, false, true, true], "each crossing evicts the resident installed longest ago");
    }

    [Fact]
    public void An_update_keeps_the_age_of_its_first_install_so_it_is_evicted_before_a_younger_entry()
    {
        var memo = new VerdictMemo(capacity: 2);
        var (older, younger) = (Guid.NewGuid(), Guid.NewGuid());
        memo.Replace(older, null, Entry());
        memo.Replace(younger, null, Entry());
        memo.Replace(older, memo.Read(older), Entry()).Should().BeTrue();

        memo.Replace(Guid.NewGuid(), null, Entry());

        memo.Read(older).Should().BeNull("an update does not move its key to the back of the queue");
        memo.Read(younger).Should().NotBeNull();
    }

    [Fact]
    public void A_read_never_refreshes_an_entrys_age()
    {
        var memo = new VerdictMemo(capacity: 4);
        var hot = Guid.NewGuid();
        memo.Replace(hot, null, Entry());
        for (var i = 0; i < 3; i++)
        {
            memo.Replace(Guid.NewGuid(), null, Entry());
            memo.Read(hot).Should().NotBeNull();
        }

        memo.Replace(Guid.NewGuid(), null, Entry());

        memo.Read(hot).Should().BeNull("it was installed first, and reading it every time changed nothing");
    }

    [Fact]
    public void Updates_of_resident_keys_never_grow_the_eviction_queue()
    {
        var memo = new VerdictMemo(capacity: 4);
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
            memo.Replace(id, null, Entry());

        for (var i = 0; i < 1_000; i++)
            memo.Replace(ids[i % 4], memo.Read(ids[i % 4]), Entry()).Should().BeTrue();

        memo.QueueLength.Should().Be(4, "no key arrived, so nothing was evicted, and the queue must not grow either");
        memo.Enqueued.Should().Be(4, "each of the four keys joined the queue once");
    }

    [Fact]
    public void Replaces_that_install_nothing_never_grow_the_eviction_queue()
    {
        var memo = new VerdictMemo(capacity: 4);
        var stale = Entry();

        for (var i = 0; i < 1_000; i++)
            memo.Replace(Guid.NewGuid(), stale, Entry()).Should().BeFalse();

        memo.QueueLength.Should().Be(0);
        memo.Enqueued.Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(16)]
    public void Under_any_mix_of_writes_the_queue_stays_as_long_as_the_memo(int capacity)
    {
        var random = new Random(capacity);
        var memo = new VerdictMemo(capacity);
        var pool = Enumerable.Range(0, capacity * 3 + 2).Select(_ => Guid.NewGuid()).ToArray();
        var lastRead = new Dictionary<Guid, MemoEntry?>();
        var newKeys = 0;

        for (var i = 0; i < 20_000; i++)
        {
            var id = pool[random.Next(pool.Length)];
            var kind = random.Next(4);
            if (kind == 3)
                id = Guid.NewGuid();
            var absent = memo.Read(id) is null;
            var installed = kind switch
            {
                0 => memo.Replace(id, lastRead[id] = memo.Read(id), Entry()),
                1 => memo.Replace(id, lastRead.GetValueOrDefault(id), Entry()),
                _ => memo.Replace(id, null, Entry()),
            };
            if (installed && absent)
                newKeys++;

            memo.Enqueued.Should().Be(newKeys, $"step {i}: only an install of an absent key joins the queue");
            memo.QueueLength.Should().Be(memo.Count, $"step {i}");
            memo.Count.Should().BeLessThanOrEqualTo(capacity);
        }
    }

    [Fact]
    public void Across_many_crossings_the_original_entries_are_the_first_evicted_not_the_last()
    {
        const int capacity = 100;
        var memo = new VerdictMemo(capacity);
        var originals = Enumerable.Range(0, capacity).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in originals)
            memo.Replace(id, null, Entry());

        for (var i = 0; i < capacity * 5; i++)
            memo.Replace(Guid.NewGuid(), null, Entry());

        originals.Count(id => memo.Read(id) is not null).Should().Be(0,
            "every original was installed before every one of the later entries, so age-ordered eviction leaves none of them");
    }

    [Fact]
    public void Every_crossing_keeps_the_entry_it_just_installed()
    {
        var memo = new VerdictMemo(capacity: 4);

        for (var i = 0; i < 64; i++)
        {
            var id = Guid.NewGuid();
            var entry = Entry();
            memo.Replace(id, null, entry);

            memo.Read(id).Should().BeSameAs(entry, $"install {i}");
        }
    }

    [Fact]
    public void Replacing_an_existing_key_at_capacity_evicts_nothing()
    {
        var memo = new VerdictMemo(capacity: 4);
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var installed = ids.Select(id => (Id: id, Entry: Entry())).ToArray();
        foreach (var (id, entry) in installed)
            memo.Replace(id, null, entry);

        memo.Replace(installed[0].Id, installed[0].Entry, Entry()).Should().BeTrue();

        memo.Count.Should().Be(4);
        ids.Should().AllSatisfy(id => memo.Read(id).Should().NotBeNull());
    }

    [Fact]
    public void Across_many_crossings_the_count_stays_between_the_eviction_floor_and_capacity()
    {
        const int capacity = 16;
        var memo = new VerdictMemo(capacity);
        var floor = capacity + 1 - (capacity + 1) / 2;
        var crossed = false;

        for (var i = 0; i < capacity * 5; i++)
        {
            memo.Replace(Guid.NewGuid(), null, Entry());
            crossed |= i >= capacity;
            memo.Count.Should().BeLessThanOrEqualTo(capacity);
            if (crossed)
                memo.Count.Should().BeGreaterThanOrEqualTo(floor);
        }
    }

    [Fact]
    public async Task Concurrent_capacity_crossings_never_take_the_count_outside_its_bounds()
    {
        const int capacity = 64;
        const int writers = 16;
        const int installsEach = 64;
        var floor = capacity + 1 - (capacity + 1) / 2;
        var memo = new VerdictMemo(capacity);
        for (var i = 0; i < capacity - 4; i++)
            memo.Replace(Guid.NewGuid(), null, Entry());

        using var start = new Barrier(writers);
        var installs = Enumerable.Range(0, writers).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            var (installed, counts) = (0, new List<int>(installsEach));
            for (var k = 0; k < installsEach; k++)
            {
                installed += memo.Replace(Guid.NewGuid(), null, Entry()) ? 1 : 0;
                counts.Add(memo.Count);
            }

            return (Installed: installed, Counts: counts);
        }, TaskCreationOptions.LongRunning)).ToArray();

        var results = await Task.WhenAll(installs).WaitAsync(Deadline);

        results.Should().AllSatisfy(r => r.Installed.Should().Be(installsEach, "every id was new, so every install lands"));
        results.SelectMany(r => r.Counts).Should().HaveCount(writers * installsEach)
            .And.OnlyContain(c => c >= floor && c <= capacity);
        memo.Count.Should().BeInRange(floor, capacity);
    }

    [Fact]
    public async Task A_read_racing_installs_that_grow_the_memo_still_finds_every_entry_nobody_removed()
    {
        const int writers = 4;
        const int readers = 4;
        var memo = new VerdictMemo(capacity: 1_000_000);
        var fixedEntries = Enumerable.Range(0, 64).Select(_ => (Id: Guid.NewGuid(), Entry: Entry())).ToArray();
        foreach (var (id, entry) in fixedEntries)
            memo.Replace(id, null, entry);

        using var start = new Barrier(writers + readers);
        var writing = 0;
        var installs = Enumerable.Range(0, writers).Select(_ => Task.Factory.StartNew(() =>
        {
            Interlocked.Increment(ref writing);
            start.SignalAndWait();
            for (var k = 0; k < 50_000; k++)
                memo.Replace(Guid.NewGuid(), null, Entry());
            Interlocked.Decrement(ref writing);
        }, TaskCreationOptions.LongRunning)).ToArray();
        var misses = Enumerable.Range(0, readers).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            var missed = 0;
            while (Volatile.Read(ref writing) > 0)
                foreach (var (id, entry) in fixedEntries)
                    missed += ReferenceEquals(memo.Read(id), entry) ? 0 : 1;
            return missed;
        }, TaskCreationOptions.LongRunning)).ToArray();

        await Task.WhenAll(installs).WaitAsync(Deadline);
        var missed = await Task.WhenAll(misses).WaitAsync(Deadline);

        missed.Should().AllSatisfy(m => m.Should().Be(0));
    }

    [Fact]
    public async Task Of_many_writers_that_read_the_same_entry_exactly_one_installs()
    {
        const int writers = 16;
        var memo = new VerdictMemo(capacity: 8);
        var id = Guid.NewGuid();
        var read = Entry();
        memo.Replace(id, null, read);
        var candidates = Enumerable.Range(0, writers).Select(_ => Entry()).ToArray();

        using var start = new Barrier(writers);
        var outcomes = candidates.Select(candidate => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            return memo.Replace(id, read, candidate);
        }, TaskCreationOptions.LongRunning)).ToArray();

        var installed = await Task.WhenAll(outcomes).WaitAsync(Deadline);

        installed.Count(i => i).Should().Be(1);
        var winner = candidates[Array.IndexOf(installed, true)];
        memo.Read(id).Should().BeSameAs(winner);
    }

    [Fact]
    public void Two_writers_that_both_observed_absent_can_both_install_across_an_intervening_eviction()
    {
        var memo = new VerdictMemo(capacity: 2);
        var x = Guid.NewGuid();
        var observedByFirst = memo.Read(x);
        var observedBySecond = memo.Read(x);
        observedByFirst.Should().BeNull();

        var second = memo.Replace(x, observedBySecond, Entry());
        memo.Replace(Guid.NewGuid(), null, Entry());
        memo.Replace(Guid.NewGuid(), null, Entry());
        memo.Read(x).Should().BeNull("x was evicted");
        var first = memo.Replace(x, observedByFirst, Entry());

        (first && second).Should().BeTrue("both writers observed absent, and the slot genuinely was absent again by the time each one wrote");
    }

    [Fact]
    public void A_writer_whose_read_entry_was_evicted_before_its_replace_call_never_installs()
    {
        var memo = new VerdictMemo(capacity: 2);
        var x = Guid.NewGuid();
        memo.Replace(x, null, Entry());
        var read = memo.Read(x);
        memo.Replace(Guid.NewGuid(), null, Entry());
        memo.Replace(Guid.NewGuid(), null, Entry());

        var outcomes = Enumerable.Range(0, 4).Select(_ => memo.Replace(x, read, Entry())).ToArray();

        outcomes.Count(installed => installed).Should().Be(0, "x no longer holds what this writer read, so every retry with that stale reference is dropped");
    }

    [Fact]
    public void AsSaved_copies_the_entry_marked_saved_and_leaves_the_original_as_it_was()
    {
        var unsaved = new MemoEntry("d", "h", Verdict(), saved: false);

        var saved = unsaved.AsSaved();

        saved.Saved.Should().BeTrue();
        saved.Should().NotBeSameAs(unsaved);
        (saved.DefinitionDigest, saved.DependencyHash, saved.Verdict).Should().Be(("d", "h", unsaved.Verdict));
        unsaved.Saved.Should().BeFalse();
    }

    [Fact]
    public void A_capacity_below_one_is_refused()
    {
        var act = () => new VerdictMemo(capacity: 0);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("capacity");
    }

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static MemoEntry Entry() => new("d", "h", Verdict(), saved: true);

    private static CompilationVerdict Verdict() =>
        new(Guid.NewGuid(), "d", "h", true, [], [], DependencyFingerprint.CompilerVersion, CompilationServiceTests.T0);
}

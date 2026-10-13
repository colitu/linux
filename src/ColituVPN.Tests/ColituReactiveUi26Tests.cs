using System.Collections.Specialized;
using System.ComponentModel;
using AwesomeAssertions;
using ReactiveUI;
using ReactiveUI.Primitives;
using ServiceLib.Base;
using ServiceLib.Events;
using ServiceLib.Models.Dto;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// ReactiveUI 26 (2026-10-13): [Reactive] properties come from the source generator (no Fody),
/// DynamicData's collections are replaced by BulkObservableCollection, Unit by RxVoid.
/// </summary>
public class ColituReactiveUi26Tests
{
    [Fact]
    public void ReactiveProperty_RaisesPropertyChanged()
    {
        var model = new ProfileItemModel();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.DelayVal = "120 ms";
        model.DelayVal = "120 ms";
        model.Delay = 120;

        changed.Should().Equal(nameof(ProfileItemModel.DelayVal), nameof(ProfileItemModel.Delay));
        model.DelayVal.Should().Be("120 ms");
    }

    [Fact]
    public void WhenAnyValue_FollowsAReactiveProperty()
    {
        var model = new ProfileItemModel();
        var seen = new List<string>();
        using var _ = model.WhenAnyValue(x => x.SpeedVal).Subscribe(value => seen.Add(value ?? "<null>"));

        model.SpeedVal = "1 MB/s";

        seen.Should().Equal("<null>", "1 MB/s");
    }

    [Fact]
    public void BulkObservableCollection_AddRangeIsOneReset_ReplaceIsOneReplace()
    {
        var items = new BulkObservableCollection<int>();
        var actions = new List<NotifyCollectionChangedAction>();
        items.CollectionChanged += (_, e) => actions.Add(e.Action);

        items.AddRange([1, 2, 3]);
        items.Replace(2, 20).Should().BeTrue();
        items.Replace(99, 1).Should().BeFalse();

        items.Should().Equal(1, 20, 3);
        actions.Should().Equal(NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Replace);
    }

    [Fact]
    public void EventChannel_PublishesRxVoid()
    {
        var channel = new EventChannel<RxVoid>();
        var count = 0;
        using var _ = channel.AsObservable().Subscribe(__ => count++);

        channel.Publish();
        channel.Publish(RxVoid.Default);

        count.Should().Be(2);
        new Action(() => new EventChannel<int>().Publish()).Should().Throw<InvalidOperationException>();
    }
}

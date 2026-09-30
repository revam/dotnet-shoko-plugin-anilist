using System.Reflection;
using System.Runtime.CompilerServices;
using Shoko.Abstractions.Actions;
using Shoko.Abstractions.ScheduledActions;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

/// <summary>
/// The rules the core enforces on actions and scheduled actions at start-up,
/// which take the whole server down when broken, and the scheduled actions'
/// IDs, which follow their type names.
/// </summary>
public class AnilistActionTests
{
    private static IEnumerable<Type> Actions()
        => typeof(Plugin).Assembly.GetTypes().Where(type => type is { IsPublic: true, IsAbstract: false } && typeof(IExecutableAction).IsAssignableFrom(type));

    private static IEnumerable<Type> ScheduledActions()
        => typeof(Plugin).Assembly.GetTypes().Where(type => type is { IsPublic: true, IsAbstract: false } && typeof(IScheduledAction).IsAssignableFrom(type));

    [Fact]
    public void TheCoresActionsArePorted()
        => Assert.Equal(5, Actions().Count());

    [Fact]
    public void EveryAction_DeclaresItsPermissionItself()
        => Assert.All(Actions(), type => Assert.Equal(type, type.GetProperty(nameof(IExecutableAction.Permission), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.DeclaringType));

    [Fact]
    public void EveryScopedAction_DerivesDirectlyFromItsBase()
        => Assert.All(Actions().Where(type => typeof(SeriesAction).IsAssignableFrom(type)), type => Assert.Equal(typeof(SeriesAction), type.BaseType));

    [Fact]
    public void TheScheduledActions_KeepTheTypeNamesTheirIDsComeFrom()
        => Assert.Equal(
            [
                "Shoko.Plugin.Anilist.Actions.PurgeAllAnilistLinksAction",
                "Shoko.Plugin.Anilist.Actions.PurgeAllUnusedAnilistAnimeAction",
                "Shoko.Plugin.Anilist.Actions.SearchForAnilistMatchesAction",
                "Shoko.Plugin.Anilist.Actions.UpdateAllAnilistAnimeAction",
                "Shoko.Plugin.Anilist.Actions.UpdateAllAnilistAnimeWithImagesAction",
            ],
            ScheduledActions().Select(type => type.FullName).Order()
        );

    [Fact]
    public void NoScheduledAction_IsAlsoAnExecutableAction()
        => Assert.All(ScheduledActions(), type => Assert.False(typeof(IExecutableAction).IsAssignableFrom(type)));

    [Fact]
    public void NoScheduledAction_HasAPublicSettableProperty()
        => Assert.All(ScheduledActions(), type => Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property => property.SetMethod is { IsPublic: true }));

    [Fact]
    public void EveryScheduledAction_OnlyRunsByHand()
        => Assert.All(ScheduledActions(), type =>
        {
            var action = (IScheduledAction)RuntimeHelpers.GetUninitializedObject(type);
            Assert.Empty(action.DefaultTriggers);
            Assert.Null(action.MinimumInterval);
            Assert.False(action.ScheduleCountsManualRuns);
        });

    [Fact]
    public void ThePurges_AskFirst()
        => Assert.All(ScheduledActions().Where(type => type.Name.StartsWith("Purge", StringComparison.Ordinal)), type =>
        {
            var action = (IScheduledAction)RuntimeHelpers.GetUninitializedObject(type);
            Assert.True(action.RequiresConfirmation);
            Assert.NotNull(action.ConfirmationMessage);
        });
}

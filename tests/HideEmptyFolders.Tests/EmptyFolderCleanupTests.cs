using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.HideEmptyFolders;
using Jellyfin.Plugin.HideEmptyFolders.Configuration;
using Jellyfin.Plugin.HideEmptyFolders.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HideEmptyFolders.Tests;

public class EmptyFolderCleanupTests
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<ILogger<EmptyFolderCleanupTask>> _logger = new();
    private readonly List<BaseItem> _items = new();
    private readonly Plugin _plugin;
    private readonly CollectionFolder _library;
    private readonly IProgress<double> _progress = new Progress<double>();

    public EmptyFolderCleanupTests()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(Path.GetTempPath());
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.GetTempPath());
        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        _plugin = new Plugin(paths.Object, serializer.Object);
        _library = new CollectionFolder
        {
            Id = Guid.NewGuid(),
            CollectionType = CollectionType.tvshows
        };
        _items.Add(_library);
        _plugin.Configuration.EnabledLibraryIds = new List<Guid> { _library.Id };

        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.SetupGet(c => c.Configuration)
            .Returns(new ServerConfiguration { DisplaySpecialsWithinSeasons = false });
        BaseItem.ConfigurationManager = configurationManager.Object;
        BaseItem.LibraryManager = _libraryManager.Object;

        _libraryManager.Setup(m => m.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => _items.SingleOrDefault(item => item.Id == id));
        _libraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) => _items
                .Where(item => query.IncludeItemTypes.Contains(item.GetBaseItemKind()))
                .Where(item => query.IsVirtualItem == null || item.IsVirtualItem == query.IsVirtualItem)
                .Where(item => query.IsMissing != false || !item.IsVirtualItem)
                .ToList());
        _libraryManager.Setup(m => m.Sort(
                It.IsAny<IEnumerable<BaseItem>>(),
                It.IsAny<User>(),
                It.IsAny<IEnumerable<ItemSortBy>>(),
                It.IsAny<SortOrder>()))
            .Returns((IEnumerable<BaseItem> items, User user,
                IEnumerable<ItemSortBy> sortBy, SortOrder sortOrder) => items);
    }

    [Fact]
    public async Task ManualRunWorksWithAutomaticCleanupDisabled()
    {
        _plugin.Configuration.AutoCleanup = false;
        var series = AddSeries();
        var season = AddSeason(series, 1);
        var task = new HideEmptyFoldersTask(
            _libraryManager.Object, NullLoggerFactory.Instance,
            NullLogger<HideEmptyFoldersTask>.Instance);

        await task.ExecuteAsync(_progress, CancellationToken.None);

        VerifyRemoved(season);
        VerifyPreserved(series);
    }

    [Fact]
    public async Task AutomaticRunStillRespectsDisabledSetting()
    {
        _plugin.Configuration.AutoCleanup = false;
        AddSeries();

        await CreateCleanup().Run(_progress, CancellationToken.None);

        _libraryManager.Verify(m => m.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
        _libraryManager.Verify(m => m.DeleteItem(
            It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task AutomaticRunRemovesEmptySeasonsAndKeepsSeries()
    {
        var series = AddSeries();
        var season = AddSeason(series, 1);

        await CreateCleanup().Run(_progress, CancellationToken.None);

        VerifyRemoved(season);
        VerifyPreserved(series);
    }

    [Fact]
    public async Task PreservesSeriesWithNoSeasonsOrEpisodes()
    {
        var series = AddSeries();

        await CreateCleanup().RunManually(_progress, CancellationToken.None);

        VerifyPreserved(series);
    }

    [Fact]
    public async Task PreservesSeriesWhenAllSeasonsAreEmpty()
    {
        var series = AddSeries();
        var first = AddSeason(series, 1);
        var second = AddSeason(series, 2);

        await CreateCleanup().RunManually(_progress, CancellationToken.None);

        VerifyPreserved(series);
        VerifyRemoved(first);
        VerifyRemoved(second);
    }

    [Fact]
    public async Task PreservesParentFoldersContainingEmptySeries()
    {
        var outer = new Folder { Id = Guid.NewGuid(), ParentId = _library.Id };
        var inner = new Folder { Id = Guid.NewGuid(), ParentId = outer.Id };
        var empty = new Folder { Id = Guid.NewGuid(), ParentId = _library.Id };
        _items.AddRange(new[] { outer, inner, empty });
        var series = AddSeries();
        series.ParentId = inner.Id;
        var season = AddSeason(series, 1);

        await CreateCleanup().RunManually(_progress, CancellationToken.None);

        VerifyPreserved(outer);
        VerifyPreserved(inner);
        VerifyPreserved(series);
        VerifyRemoved(season);
        VerifyRemoved(empty);
    }

    [Fact]
    public async Task RemovesEmptyVirtualSeasonAndKeepsVirtualSeasonWithEpisodes()
    {
        var series = AddSeries();
        var populated = AddSeason(series, 1);
        var empty = AddSeason(series, 2);
        populated.IsVirtualItem = true;
        empty.IsVirtualItem = true;
        AddEpisode(series, populated, flatFolder: true);

        await CreateCleanup().RunManually(_progress, CancellationToken.None);

        VerifyPreserved(series);
        VerifyPreserved(populated);
        VerifyRemoved(empty);
    }

    [Fact]
    public async Task RemovesEmptySeasonFromShowWithAnotherPopulatedSeason()
    {
        var series = AddSeries();
        var populated = AddSeason(series, 1);
        var empty = AddSeason(series, 2);
        AddEpisode(series, populated, flatFolder: false);

        await CreateCleanup().RunManually(_progress, CancellationToken.None);

        VerifyRemoved(empty);
        VerifyPreserved(series);
        VerifyPreserved(populated);
    }

    [Fact]
    public async Task PreservesSeasonWithFlatFolderEpisodes()
    {
        var series = AddSeries();
        var populated = AddSeason(series, 1);
        var empty = AddSeason(series, 2);
        AddEpisode(series, populated, flatFolder: true);

        await CreateCleanup().RunManually(_progress, CancellationToken.None);

        VerifyPreserved(populated);
        VerifyRemoved(empty);
    }

    [Fact]
    public async Task MissingVirtualEpisodesDoNotKeepEmptyPhysicalSeason()
    {
        var series = AddSeries();
        var populated = AddSeason(series, 1);
        var empty = AddSeason(series, 2);
        AddEpisode(series, populated, flatFolder: false);
        AddEpisode(series, empty, flatFolder: true).IsVirtualItem = true;

        await CreateCleanup().RunManually(_progress, CancellationToken.None);

        VerifyRemoved(empty);
        VerifyPreserved(populated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualRunRespectsHideEmptySeasonsSetting(bool virtualSeason)
    {
        _plugin.Configuration.HideEmptySeasons = false;
        var series = AddSeries();
        var populated = AddSeason(series, 1);
        var empty = AddSeason(series, 2);
        empty.IsVirtualItem = virtualSeason;
        AddEpisode(series, populated, flatFolder: false);

        await CreateCleanup().RunManually(_progress, CancellationToken.None);

        VerifyPreserved(empty);
    }

    private EmptyFolderCleanupTask CreateCleanup() =>
        new(_libraryManager.Object, _logger.Object);

    private Series AddSeries()
    {
        var series = new Series
        {
            Id = Guid.NewGuid(),
            ParentId = _library.Id,
            Name = "Test show",
            PresentationUniqueKey = Guid.NewGuid().ToString()
        };
        _items.Add(series);
        return series;
    }

    private Season AddSeason(Series series, int number)
    {
        var season = new Season
        {
            Id = Guid.NewGuid(),
            ParentId = series.Id,
            SeriesId = series.Id,
            IndexNumber = number,
            Name = $"Season {number}",
            PresentationUniqueKey = $"{series.PresentationUniqueKey}-{number}"
        };
        _items.Add(season);
        return season;
    }

    private Episode AddEpisode(Series series, Season season, bool flatFolder)
    {
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            ParentId = flatFolder ? series.Id : season.Id,
            SeriesId = series.Id,
            SeasonId = season.Id,
            ParentIndexNumber = season.IndexNumber,
            Name = "Test episode"
        };
        _items.Add(episode);
        return episode;
    }

    private void VerifyRemoved(BaseItem item) =>
        _libraryManager.Verify(m => m.DeleteItem(item, It.Is<DeleteOptions>(
            options => !options.DeleteFileLocation && !options.DeleteFromExternalProvider)),
            Times.Once);

    private void VerifyPreserved(BaseItem item) =>
        _libraryManager.Verify(m => m.DeleteItem(item, It.IsAny<DeleteOptions>()), Times.Never);
}

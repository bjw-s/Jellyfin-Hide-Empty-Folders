using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.HideEmptyFolders.Configuration;

namespace Jellyfin.Plugin.HideEmptyFolders;

/// <summary>
/// Removes empty folders from the library database while preserving series.
/// Files on disk are never deleted.
/// </summary>
public class EmptyFolderCleanupTask : ILibraryPostScanTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<EmptyFolderCleanupTask> _logger;

    /// <summary>
    /// Folder item kinds that should be checked for emptiness.
    /// </summary>
    private static readonly BaseItemKind[] FolderKinds =
    {
        BaseItemKind.Series,
        BaseItemKind.Season,
        BaseItemKind.BoxSet,
        BaseItemKind.Folder,
    };

    /// <summary>
    /// Leaf item kinds that count as "media content".
    /// A folder is NOT empty if it contains (directly or indirectly) any of these.
    /// </summary>
    private static readonly BaseItemKind[] MediaLeafKinds =
    {
        BaseItemKind.Episode,
        BaseItemKind.Movie,
        BaseItemKind.Audio,
        BaseItemKind.MusicVideo,
        BaseItemKind.Video,
        BaseItemKind.Trailer,
    };

    public EmptyFolderCleanupTask(
        ILibraryManager libraryManager,
        ILogger<EmptyFolderCleanupTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration is { AutoCleanup: false })
        {
            _logger.LogDebug("Auto-cleanup disabled in plugin config; skipping");
            return Task.CompletedTask;
        }

        return RunManually(progress, cancellationToken);
    }

    public Task RunManually(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        progress.Report(0);

        try
        {
            // ── Resolve which libraries to process ──────────────────
            // When EnabledLibraryIds is empty (fresh install or user checked all),
            // default to only TV Show (tvshows) libraries to avoid accidentally
            // cleaning up Movies, Music, or other media libraries.
            HashSet<Guid> effectiveLibraryIds;
            if (config?.EnabledLibraryIds is { Count: > 0 })
            {
                effectiveLibraryIds = new HashSet<Guid>(config.EnabledLibraryIds);
                _logger.LogDebug("Using {Count} explicitly configured library IDs", effectiveLibraryIds.Count);
            }
            else
            {
                effectiveLibraryIds = GetTvShowLibraryIds();
                _logger.LogInformation(
                    "No libraries explicitly configured — defaulting to {Count} TV Show library(s)",
                    effectiveLibraryIds.Count);
            }

            // ── Step 1: Get all folder-type items ──────────────────
            _logger.LogInformation("Scanning for empty folders...");

            var folderQuery = new InternalItemsQuery
            {
                IncludeItemTypes = FolderKinds,
                Recursive = true,
                AncestorIds = effectiveLibraryIds.Count > 0
                    ? effectiveLibraryIds.ToArray()
                    : Array.Empty<Guid>(),
            };

            var folders = _libraryManager.GetItemList(folderQuery);
            _logger.LogDebug("Found {Count} folder items to check", folders.Count);
            progress.Report(20);

            // ── Step 2: Find which folders have media descendants ──
            var protectedFolders = new HashSet<Guid>();

            // Query leaf media items, scoped to the relevant libraries only.
            // Without AncestorIds, this would pull every movie/episode/song
            // in the entire Jellyfin instance — thousands of items causing
            // get_OfficialRatingForComparison() spam during comparison.
            var mediaQuery = new InternalItemsQuery
            {
                IncludeItemTypes = MediaLeafKinds,
                IsVirtualItem = false,
                Recursive = true,
                AncestorIds = effectiveLibraryIds.Count > 0
                    ? effectiveLibraryIds.ToArray()
                    : Array.Empty<Guid>(),
            };

            var mediaItems = _libraryManager.GetItemList(mediaQuery);
            _logger.LogDebug("Found {Count} media items in library", mediaItems.Count);
            progress.Report(50);

            // Protect series ancestors too: deleting an empty parent folder
            // could otherwise remove a series indirectly.
            foreach (var item in mediaItems.Concat(folders.OfType<Series>()))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var parentId = item.ParentId;
                while (parentId != Guid.Empty)
                {
                    protectedFolders.Add(parentId);

                    // Walk up to the parent
                    var parent = _libraryManager.GetItemById(parentId);
                    if (parent == null) break;
                    parentId = parent.ParentId;

                    // Safety: don't walk up past collection folders
                    if (parent is CollectionFolder) break;
                }
            }
            progress.Report(70);

            _logger.LogDebug("Preserving {Count} folders containing media or series", protectedFolders.Count);

            // ── Step 3: Identify candidate empty folders ────────────
            // Folders with no media descendants are candidates for deletion.
            var candidateFolders = new List<BaseItem>();

            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip folders containing media or a preserved series.
                if (protectedFolders.Contains(folder.Id))
                    continue;

                if (folder is CollectionFolder or Series)
                    continue;

                if (folder.IsVirtualItem && folder is not Season)
                    continue;

                // Respect library selection
                var topParent = FindTopLibraryFolder(folder);
                if (topParent != null && !effectiveLibraryIds.Contains(topParent.Id))
                    continue;

                // Respect per-type settings
                var kind = folder.GetBaseItemKind();
                if (kind == BaseItemKind.Season && config is { HideEmptySeasons: false })
                    continue;
                if (kind == BaseItemKind.BoxSet && config is { HideEmptyCollections: false })
                    continue;

                candidateFolders.Add(folder);
            }

            _logger.LogDebug("Found {Count} candidate empty folders", candidateFolders.Count);
            progress.Report(75);

            // ── Step 4: Verify candidates are truly empty ──────────
            // Flat-folder episodes can bypass Season in the parent chain.
            // Use Jellyfin's season matching rather than preserving every season
            // whenever its series has any content.
            var trulyEmpty = new List<BaseItem>();

            foreach (var folder in candidateFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (folder is Season season)
                {
                    if (season.GetEpisodes(null, new DtoOptions(false), false)
                        .Any(episode => !episode.IsVirtualItem))
                    {
                        _logger.LogDebug(
                            "Preserving season {Name} — it has playable episodes",
                            folder.Name);
                        continue;
                    }
                }

                trulyEmpty.Add(folder);
            }

            _logger.LogInformation(
                "Candidates: {Candidate}, truly empty: {TrulyEmpty}",
                candidateFolders.Count,
                trulyEmpty.Count);

            // ── Step 5: Delete truly-empty folders ─────────────────
            _logger.LogInformation("Removing {Count} empty folders from library", trulyEmpty.Count);
            progress.Report(85);

            var deleteOptions = new DeleteOptions
            {
                DeleteFileLocation = false,         // NEVER delete from disk!
                DeleteFromExternalProvider = false,
            };

            int removed = 0;
            foreach (var folder in trulyEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    _logger.LogDebug("Removing empty folder: {Name} ({Path})",
                        folder.Name, folder.Path);
                    _libraryManager.DeleteItem(folder, deleteOptions);
                    removed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove folder: {Name}", folder.Name);
                }
            }

            progress.Report(100);
            _logger.LogInformation("Hide Empty Folders complete: {Removed} folders removed", removed);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Empty folder cleanup was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during empty folder cleanup");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the IDs of all CollectionFolders whose CollectionType is "tvshows".
    /// Used as the default library filter when no libraries are explicitly configured.
    /// </summary>
    private HashSet<Guid> GetTvShowLibraryIds()
    {
        var tvIds = new HashSet<Guid>();

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.CollectionFolder },
            IsVirtualItem = false,
        };

        var collectionFolders = _libraryManager.GetItemList(query);
        foreach (var folder in collectionFolders)
        {
            if (folder is CollectionFolder cf
                && cf.CollectionType == Jellyfin.Data.Enums.CollectionType.tvshows)
            {
                tvIds.Add(cf.Id);
                _logger.LogDebug("Default-enabled TV Show library: {Name} ({Id})", cf.Name, cf.Id);
            }
        }

        return tvIds;
    }

    /// <summary>
    /// Walks up the parent chain to find the top-level CollectionFolder.
    /// </summary>
    private BaseItem? FindTopLibraryFolder(BaseItem item)
    {
        var current = item;
        while (current != null)
        {
            if (current.GetBaseItemKind() == BaseItemKind.CollectionFolder)
                return current;

            if (current.ParentId == Guid.Empty)
                break;

            current = _libraryManager.GetItemById(current.ParentId);
        }

        return null;
    }
}

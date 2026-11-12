<p align="center">
  <img src="logo.png" alt="Hide Empty Folders" width="200">
</p>

# Hide Empty Folders

A Jellyfin plugin that keeps your library clean. Empty seasons, box sets, and
other empty folders are removed from the library view automatically. Series
remain visible even when they have no episodes. Nothing is ever deleted from disk.

[![Listed on JellyWatch Hub](https://jellywatch.app/hub/hide-empty-folders/badge.svg)](https://jellywatch.app/hub/hide-empty-folders)

---

## Installation

### Repository (recommended)

Add this URL to Jellyfin under **Dashboard → Plugins → Repositories → Add**:

```
https://raw.githubusercontent.com/CapstonPeters/Jellyfin-Hide-Empty-Folders/main/manifest.json
```

Then go to **Catalog**, find *Hide Empty Folders*, and click Install. Restart Jellyfin.

### Manual

1. Download the latest ZIP from [Releases](https://github.com/CapstonPeters/Jellyfin-Hide-Empty-Folders/releases)
2. Extract into your Jellyfin plugin folder — the resulting directory should look like:
   ```
   plugins/Hide Empty Folders/
   ├── Jellyfin.Plugin.HideEmptyFolders.dll
   ├── thumb.png
   └── meta.json
   ```
3. Restart Jellyfin

---

## Configuration

Open **Dashboard → Plugins → Hide Empty Folders → Settings**.

| Setting | What it does |
|---|---|
| **Libraries** | Checkboxes for each of your libraries. Only checked libraries are processed. By default, only TV Show libraries are checked — enable others explicitly if you want Movies, Music, etc. cleaned too. |
| **Run automatically** | Runs cleanup after every library scan. Turn off if you only want to trigger it manually. |
| **Hide empty seasons** | TV seasons with zero episodes are removed from view. |
| **Hide empty collections** | Box sets and collections with no items are removed from view. |

### Manual trigger

Go to **Dashboard → Scheduled Tasks → Hide Empty Folders** and click the play button.
Manual runs work even when **Run automatically** is disabled.

---

## How it works

After a library scan completes (or you trigger it manually), the plugin:

1. Finds all media items across your selected libraries
2. Protects series and folders containing media or series
3. Removes database entries for empty seasons and other unprotected empty folders

The plugin uses `DeleteFileLocation = false` — folder entries are removed from Jellyfin's database only. Your files stay exactly where they are.

Collection folders (the top-level "Movies", "TV Shows" containers) and series are
never removed, even when every season of a series is empty.

**Seasons containing episodes are preserved.** Jellyfin's season matching also
recognizes episodes stored directly in a show folder. Both physical and virtual
seasons with no playable episodes can be removed, even when another season of
the same show contains episodes.

---

## What to expect

**Jellyfin is the party that adds empty folders.** The plugin only removes them from the library database — it never touches files on disk.

- **If episodes are added to an empty season:** Jellyfin's real-time monitoring
  (or a library scan) rediscovers the season. Seasons with playable episodes are
  preserved, and the series stays visible throughout.
- **If you run a manual library scan:** The plugin runs immediately after the scan completes and cleans up any empty folders.
- **If you trigger the scheduled task** (Dashboard → Scheduled Tasks → Hide Empty Folders): It runs the same cleanup on demand.
- **Files on disk are never deleted.** The plugin uses `DeleteFileLocation = false`
  and preserves series and CollectionFolder (library root) items.

---

## Building

Requires the .NET 10 SDK. This release targets Jellyfin 12.0.0 and is not compatible with Jellyfin 10.11.x.

```bash
dotnet restore
dotnet build --configuration Release
```

The output DLL goes in `bin/Release/net10.0/`. Bundle it with `thumb.png` and `meta.json` for distribution.

Run the regression tests with:

```bash
dotnet test tests/HideEmptyFolders.Tests/HideEmptyFolders.Tests.csproj --configuration Release
```

---

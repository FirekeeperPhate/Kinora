# Kinora

A desktop library for movies (and, soon, TV shows) kept in folders on a disk or a NAS.
Covers, metadata, light and dark theme; Play opens the file with the player the OS associates
with it. Windows, Linux and macOS (Avalonia 12, .NET 10).

## What it does now

- **Library folders**: as many as you like. Each movie in its own folder, collection folders
  (`Alien Collection/Alien (1979)/…`), or loose files in the root.
- **Local metadata first**: Kodi / Emby / Jellyfin `.nfo` files (`movie.nfo` and `<video>.nfo`:
  the newer wins, the older fills the gaps), posters, fanart and clear logos with the usual names,
  subtitles next to the video. Without an nfo, title and year come from the folder or file name;
  `[imdbid-tt…]` and `{tmdb-…}` in names are read too.
- **Fast on a NAS**: the library opens from a local SQLite database and a local cache of resized
  posters, never touching the share. A scan enters only the folders whose time changed; a full
  scan reads them all. 472 movies on a NAS: first scan 8.5 s (posters included), the next 0.1 s.
- **Offline-safe**: a root that doesn't answer (NAS asleep or off) deletes nothing; its movies are
  marked offline.
- **Watched** flags survive rescans, and a movie moved outside Kinora keeps it (by IMDb/TMDB id).
- Search across title, original title, year, director, collection and cast; accents and
  punctuation don't matter. Sort by title (articles ignored), year, date added, rating.
- Folders that are mounted somewhere else later (another drive letter, another OS): change the
  root's path in Settings, nothing is scanned again. Paths are stored relative to the root.

## Online metadata (TMDB)

With a TMDB API key (Settings), what the folders don't say comes from The Movie Database:

- movies without ids are searched by title and year, and taken only when the match is sure
  (same title, year within one); the rest wait in **Not identified**, where **Identify…** lets
  you choose;
- movies with gaps (no plot, genres, cast, poster or fanart) get them filled; what the folder
  says always wins, and a rescan keeps what came from TMDB (it is kept apart, in its own table,
  and merged again whenever a movie is rebuilt from its folder);
- missing posters are downloaded into Kinora's cache; backdrops and logos when a movie page opens;
- with **Save downloaded metadata and pictures in the movie folders** on (off by default) a
  `movie.nfo` and the missing pictures are written next to the movie; existing files are never
  replaced.

The key is stored encrypted for the Windows user (DPAPI); on Linux and macOS in a settings file
only the user can read.

## Planned

1. TV shows: series, seasons, episodes.
2. Subtitles from OpenSubtitles (by file hash first).
3. Moving movies between library folders.
4. Packages for Windows (Inno Setup), Linux (AppImage / tar.gz) and macOS (.app).

## Building

```
dotnet build Kinora.slnx
dotnet test --project tests/Kinora.Tests
dotnet run --project src/Kinora
```

`bin` and `obj` go to `%LOCALAPPDATA%\KinoraBuild` (or `~/.cache/KinoraBuild`), not next to the
sources: the repository lives in a synced folder.

Kinora's data (database, settings, picture cache) is in `%LOCALAPPDATA%\Kinora`,
`~/.local/share/Kinora` or `~/Library/Application Support/Kinora`. Set `KINORA_DATA` to use
another folder, e.g. to try things without touching the real library.

The icon is drawn by `tools/IconMaker`:

```
dotnet run --project tools/IconMaker -- src/Kinora/Assets
```

## Layout

- `src/Kinora.Core`: models, scanning (nfo, names, artwork), SQLite, image cache, launcher. No UI.
- `src/Kinora`: the Avalonia app (MVVM with CommunityToolkit.Mvvm).
- `tests/Kinora.Tests`: xUnit v3 (Microsoft Testing Platform, see `global.json`).

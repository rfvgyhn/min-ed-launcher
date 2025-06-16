# Changelog

## [unreleased]

### Security
- Address [CVE-2025-30399] by building with latest version of the .Net SDK (>= 8.0.314)

## [0.12.0] - 2025-05-06

### Changes
- Don't attempt to start external processes in a minimized state

  Most 3rd party apps are meant to be viewed while playing. Should also fix 3rd party apps that don't render properly
  when starting minimized ([#161]).

### Bug Fixes
- Add support for downloading stealth updates ([#163])

  The default launcher has the ability to apply updates that aren't marked as a new version. These updates may also not 
  be pushed to Steam and Epic which would prevent users of these stores from receiving the update.

## [0.11.3] - 2025-04-08

### Enhancements
- Make argument parsing more generic which will allow for more Linux compatibility tools to be supported ([#151])
- Use GitHub Actions to generate [artifact attestations] that establish build provenance for release artifacts.
  This adds another way to ensure the launcher is built from source without any modifications.

  `gh attestation verify min-ed-launcher_v[VERSION]_win-x64.zip -R rfvgyhn/min-ed-launcher`

### Bug Fixes
- Fix exception being thrown when specifying shutdown processes ([#156])
- Fix invalid Epic login details due to format change ([#157])

## [0.11.2] - 2025-01-14

### New Features
- Add ability to keep applications open after launcher exits. Can be useful to review your recent activity in apps like 
  [EDDiscovery].
  
  Set the new `keepOpen` property to true in your [settings file].
  ```json
  "processes": [{
    "fileName": "path\\to\\exe",
    "keepOpen": true  
  }]
  ```
- Allow skipping the prompt to install a product when `/autorun` isn't specified with the new flag `/skipInstallPrompt`.
  
### Enhancements
- Read additional processes' STDOUT/ERR asynchronously. This should allow [EDOMH] to launch without locking up.
- Do a better job of scrubbing stored frontier passwords in log file

## [0.11.1] - 2024-07-10

### Enhancements
- Add `waitForQuit` arg to `/autoquit` (i.e. `/autoquit waitForQuit`). This will let users opt in to the old behavior
  of `/autoquit` which is to wait for Elite to exit before closing the launcher.
- Make reading [log file] a bit easier by writing a separator between launcher runs.

### Security
- Address [CVE-2024-30105] and [CVE-2024-38095] by building with latest version of .Net SDK (8.0.303)

## [0.11.0] - 2024-07-08

### New Features
- Add ability to restart applications when the `/restart` flag is specified. Useful for when running 3DMigoto/EDHM_UI in VR.
  Set the new `restartOnRelaunch` property to true in your [settings file].
  ```json
  "processes": [{
    "fileName": "path\\to\\exe",
    "restartOnRelaunch": true  
  }]
  ```
  
### Enhancements
- Enable restart feature for Epic users. It's still not as seamless as non-Epic accounts. Requires the usage of [Legendary]
  or [Heroic]. Once you've logged in with either, you can go back to using the normal Epic launcher if you wish. It will
  require re-logging in every few days though, so it may be preferable to just stick with the alternate launchers. 
  [The Wiki](https://github.com/rfvgyhn/min-ed-launcher/wiki/Using-Legendary-on-Windows) has instructions on how to setup 
  Legendary.
- Added an [icon](resources/min-ed-launcher.svg) for the app. Linux users can check the [readme](README.md#icon-on-linux) 
  for setup instructions.
- The launcher will now exit instead of waiting for Elite to exit if the following conditions are met:
  1. `/autoquit` is specified
  2. `/restart` is not specified
  3. No external apps specified in `settings.json` (`processes`, `shutdownProcesses`)

## [0.10.1] - 2024-05-03

### Bug Fixes
- Fix always installing uninstalled products when using a Frontier login with `autoUpdate` enabled

## [0.10.0] - 2024-05-01

### New Features
- Add ability to download products if they aren't yet installed
- Show a more helpful message instead of generic JSON error when Frontier API couldn't verify game ownership.
  
  This error happens intermittently with Steam licenses.

### Enhancements
- The launcher will now keep going even if there's an error when checking for a launcher update.

### Breaking changes
- `/vr` no longer implies `/autorun`. This allows for users to select a game version while also specifying VR mode.

  If you relied on this behavior, you'll need to append `/autorun` to your launch options going forward.

### Bug fixes
- Prevent bootstrapper from not logging errors if log directory didn't already exist

## [0.9.0] - 2023-09-12

### New Features
- Add ability to specify cache directory in settings file since Windows users don't have an environment variable
  like `XDG_CACHE_DIR` on Linux. Add `cacheDir` to your [settings file].
- Add ability to delay ED launch for processes that have a long startup time. Can be configured via the new
  `gameStartDelay` property in the [settings file]. Specify a value in seconds.
- Add ability to delay shutdown for processes that need time to do stuff before exiting. Can be configured via the
  new `shutdownDelay` property in the [settings file]. Specify a value in seconds.
- Launcher will now prompt for product selection after the game exits when `/autoquit` isn't specified. This will
  allow users to select a different product without having to re-launch the launcher which more closely aligns with
  the default launcher's behavior.

### Enhancements
- Check if disk has enough free space before attempting to download game updates

### Bug Fixes
- Fix launcher not shutting down when a startup process doesn't properly shutdown or takes too long to shutdown.
  The timeout for taking too long can be configured via the new `shutdownTimeout` property in the [settings file].
  It defaults to 10 seconds.

### Security
- Address [CVE-2023-36792], [CVE-2023-36793], [CVE-2023-36794], [CVE-2023-36796] and [CVE-2023-36799] by building with latest version of .net SDK (7.0.401)

### Misc
- Update Frontier auth API to use v3.0 endpoints to match changes in default launcher 

## [0.8.2] - 2023-04-12

### Security
- Address [CVE-2023-28260] (Windows only) by building with latest version of .net SDK (7.0.203)

### Bug Fixes
- Fix crash when HOST_LC_ALL contains a `.` character
- Fix not all startup processes being terminated properly
- Fix crash when attempting to terminate an already terminated process

## [0.8.1] - 2023-02-14

### Bug Fixes
- Fix crash when checking for launcher updates due to missing types

## [0.8.0] - 2023-02-14

### New Features
- Add support for running processes on launcher shutdown

  To make use of this feature, add `shutdownProcesses` to your [settings file]. It has the same format
  as startup processes.
- Check for updates to the launcher in addition to checking for game updates

  Defaults to on but can be disabled by setting `checkForLauncherUpdates` to `false` in your 
  [settings file]. This is mainly to inform users of security related updates.

### Enhancements
- When using the restart feature, pressing `<space>` will immediately restart instead of having to wait
  for the timeout to finish

### Breaking changes
- Removed support for reading from STDIN. This will affect linux users launching via legendary.
  - Instead of piping legendary's arguments into min-ed-launcher, use command substitution instead
    
    `WINEPREFIX=/your/wine/prefix /path/to/MinEdLauncher $(legendary launch --dry-run 9c203b6ed35846e8a4a9ff1e314f6593 2>&1 | grep "Launch parameters" | cut -d':' -f 3-) /autorun /edh4 /autoquit`

### Security
- Address [CVE-2023-21808] by building with latest version of .net SDK (7.0.200)

### Bug Fixes
- Fixed an issue where the launcher would hang because no data was available in STDIN.

## [0.7.5] - 2022-11-21

### Bug Fixes
- Fix crash due to missing types (introduced by upgrading to .net 7 in the last release)

## [0.7.4] - 2022-11-18

### Enhancements
- Upgrade to .net 7
    - Among other things, reduces binary size by about 23%

### Bug Fixes
- Fix crash when trying to login with frontier credentials and current working directory isn't the same as Elite's game directory

## [0.7.3] - 2022-10-20

### Enhancements
- Add ability to run the launcher without launching any processes via the `/dryrun` flag
- Don't close console window if an error occurred even if `/autoquit` is specified. Makes it easier to see what went wrong compared to having to open the log file
- Reduce Bootstrapper file size by about 9x

### Bug Fixes
- Fix game client not shutting down because of pending stdout/stderr stream (usually when running via lutris)

## [0.7.2] - 2022-10-10

### Enhancements
- Support launching the game via custom version of wine (i.e. lutris)

### Bug Fixes
- Fix crash when STDIN was interpreted as null (running windows version via wine)
- Fix console window not opening when launching via Bootstrapper.exe
- Fix formatting of redacted steam token (makes logs easier to read)
- Fix crash when running via Epic due to updated EosIF.dll
- Fix not correctly parsing quoted args when reading from STDIN (e.g. piping from legendary when path to game has spaces)

## [0.7.1] - 2022-09-25

### Bug Fixes
- Fix game not launching properly on Linux + Steam because of new Proton process `steam-launch-wrapper`

## [0.7.0] - 2022-09-22

### New Features
- Provide extra products to the authorized product list. Useful for launching Horizons 4.0 when you own the Odyssey DLC.

    To make use of this feature, users will need to add the following to their [settings file] file to include the following:
    ```json
    "additionalProducts": [{
        "filter": "edh4",
        "directory": "elite-dangerous-odyssey-64",
        "serverargs": "",
        "gameargs": "SeasonTwo",
        "sortkey": "04",
        "product_name": "Elite Dangerous: Horizons (4.0)",
        "product_sku": "FORC-FDEV-DO-38-IN-40"
    }]
    ```

### Enhancements
- Support `.rdr` files when looking for a product's directory
- Show progress indicator when verifying integrity of game files

### Bug Fixes
- Fix crash when no products are available to play

## [0.6.0] - 2022-09-15

### New Features
- Read arguments from STDIN which allows for piping info from other apps (e.g. legendary)

### Enhancements
- Added support for detecting wine usage from command line args
- Better logging for when failing to get an Epic auth token
- Upgrade to .Net 6
- Add Horizons 4.0 launch flag to default settings (`/edh4`)

  Users upgrading from previous versions will need to manually [update](https://github.com/rfvgyhn/min-ed-launcher/commit/c1b64c4a834dcf59fe90ff7b22e88ce6aeffd7bc#diff-c816007aa9ea03a01c4190efee0827d0ac32ff978fe02981844c1b5213d55e49) their [settings file] or delete it and let it be autogenerated to include the new flag
### Misc
- Log file is placed in a standard location (`%LOCALAPPDATA%`, `$XDG_STATE_HOME`).

Epic users on Linux should now have an easier time launching the game by utilizing [legendary]
to automatically generate an exchange code instead of having to manually copy it from your browser.

Example usage:
```
legendary launch --dry-run 9c203b6ed35846e8a4a9ff1e314f6593 2> >(grep "Launch parameters") | cut -d':' -f 3- | WINEPREFIX=/your/wine/prefix /path/to/MinEdLauncher /autorun /edh /autoquit
```

## [0.5.4] - 2021-10-19

### Bug Fixes
- Add `User-Agent` http header when requesting an Epic auth token

## [0.5.3] - 2021-06-09

### Bug Fixes
- Implement _working_ temporary workaround for launcher failing when Steam starts the executable via `reaper`
- Fix EDLaunch.exe path being treated as product whitelist item

## [0.5.2] - 2021-06-09

### Security
- Address [CVE-2021-31957] by building with latest version of .net SDK (5.0.301)

### Bug Fixes
- Implement temporary workaround for launcher failing when Steam starts the executable via `reaper`

## [0.5.1] - 2021-06-04

### Enhancements

- Automatically fix Odyssey filter by providing correct override in default `settings.json`

  Users upgrading from previous versions will need to manually update their `settings.json` file or delete it and let it be auto-created again.
  
  Manual update should include the following:
  ```json
  "filterOverrides": [
        { "sku": "FORC-FDEV-DO-1000", "filter": "edo" }
    ]
  ```

### Bug Fixes

- Fix filter overrides not being case-insensitive
- Fix `/novr` flag being treated as a product whitelist

## [0.5.0] - 2021-06-03

### Breaking Changes
- Restart option has moved to the `/restart delay` argument and is no longer specified in the config file.
  
  This allows for creating separate shortcuts, one for normal gameplay and one for restarting.
  
  Instead of specifying `restart: { enabled: true, shutdownTimeout: 3 }`, modify your launch options to include `/restart 3`.

### New Features
- Add ability to override a product's filter via the config file
  
  Useful for when FDev makes a copy/paste error for a new product (i.e. when they released Odyssey with an "edh" filter instead of "edo")

## [0.4.0] - 2021-05-11

### New Features
- Add support for Frontier accounts via the `/frontier` argument. This includes logging in with a single game installation (e.g. Steam) and
  keeping the game up-to-date if you don't use Steam or Epic. See the readme for instructions on how to use this feature.
  
### Security
- Address [CVE-2021-31204] by building with latest version of .net (5.0.6)

## [0.3.1] - 2021-03-31

### New Features
- Add ability to select product to run

### Enhancements
- Add workaround for Steam always setting `$LC_ALL` to `C` which prevented the correct game language from being selected
- Include launcher's directory as potential Elite Dangerous install location

### Bug Fixes
- Properly parse proton when it's in `compatibilitytools.d` dir. Custom versions of proton, such as [Proton-GE], are stored here.
- Fix `$XDG_CONFIG_DIR` not always being parsed properly
- Fix not being able to handle a variable amount of steam linux runtime args (i.e. `--deploy=soldier --suite=soldier --verb=waitforexitandrun` vs `--verb=waitforexitandrun`)

## [0.3.0] - 2020-12-28

### New Features

- Add support for specifying Elite's language instead of just using the system default

## [0.2.0] - 2020-12-13

### New Features

- Add support for launching via Proton 5.13 and greater which runs via Steam Linux Runtime

### Bug Fixes

- Fix not being able to find libsteam_api.so on linux
- Fix invalid path on windows when looking for a fallback installation path 
- Fix incorrect linux path in setup instructions

## [0.1.1] - 2020-12-08

### Enhancements

- Log settings file location

### Bug Fixes

- Fix windows build looking in wrong location for settings file

## [0.1.0] - 2020-12-05

Initial release

[unreleased]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.12.0...HEAD
[0.12.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.11.3...v0.12.0
[0.11.3]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.11.2...v0.11.3
[0.11.2]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.11.1...v0.11.2
[0.11.1]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.11.0...v0.11.1
[0.11.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.10.1...v0.11.0
[0.10.1]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.10.0...v0.10.1
[0.10.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.9.0...v0.10.0
[0.9.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.8.2...v0.9.0
[0.8.2]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.8.1...v0.8.2
[0.8.1]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.7.5...v0.8.0
[0.7.5]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.7.4...v0.7.5
[0.7.4]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.7.3...v0.7.4
[0.7.3]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.7.2...v0.7.3
[0.7.2]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.7.1...v0.7.2
[0.7.1]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.7.0...v0.7.1
[0.7.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.5.4...v0.6.0
[0.5.4]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.5.3...v0.5.4
[0.5.3]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.5.2...v0.5.3
[0.5.2]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.3.1...v0.4.0
[0.3.1]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/67d8c3f...v0.1.0
[Proton-GE]: https://github.com/GloriousEggroll/proton-ge-custom
[CVE-2021-31204]: https://github.com/dotnet/announcements/issues/185
[CVE-2021-31957]: https://github.com/dotnet/announcements/issues/189
[CVE-2023-21808]: https://github.com/dotnet/announcements/issues/247
[CVE-2023-28260]: https://github.com/dotnet/announcements/issues/250
[CVE-2023-36792]: https://github.com/dotnet/announcements/issues/271
[CVE-2023-36793]: https://github.com/dotnet/announcements/issues/273
[CVE-2023-36794]: https://github.com/dotnet/announcements/issues/272
[CVE-2023-36796]: https://github.com/dotnet/announcements/issues/274
[CVE-2023-36799]: https://github.com/dotnet/announcements/issues/275
[CVE-2024-30105]: https://github.com/dotnet/announcements/issues/315
[CVE-2024-38095]: https://github.com/dotnet/announcements/issues/312
[CVE-2025-30399]: https://github.com/dotnet/announcements/issues/362
[legendary]: https://github.com/derrod/legendary
[heroic]: https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher
[settings file]: README.md#settings
[log file]: README.md#troubleshooting
[EDDiscovery]: https://github.com/EDDiscovery/EDDiscovery
[EDOMH]: https://github.com/jixxed/ed-odyssey-materials-helper
[artifact attestations]: https://docs.github.com/en/actions/security-for-github-actions/using-artifact-attestations/using-artifact-attestations-to-establish-provenance-for-builds
[#151]: https://github.com/rfvgyhn/min-ed-launcher/issues/151
[#156]: https://github.com/rfvgyhn/min-ed-launcher/issues/156
[#157]: https://github.com/rfvgyhn/min-ed-launcher/issues/157
[#161]: https://github.com/rfvgyhn/min-ed-launcher/issues/161
[#163]: https://github.com/rfvgyhn/min-ed-launcher/issues/163
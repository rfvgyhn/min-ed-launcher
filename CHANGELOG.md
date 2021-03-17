# Changelog

## [Unreleased]

### New Features
* Add workaround for Steam always setting `$LC_ALL` to `C` which prevented the correct game language from being selected

### Bug Fixes
* Properly parse proton when it's in `compatibilitytools.d` dir. Custom versions of proton, such as [Proton-GE], are stored here.
* Fix `$XDG_CONFIG_DIR` not always being parsed properly

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

[unreleased]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/rfvgyhn/min-ed-launcher/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/rfvgyhn/min-ed-launcher/compare/67d8c3f...v0.1.0
[Proton-GE]: https://github.com/GloriousEggroll/proton-ge-custom
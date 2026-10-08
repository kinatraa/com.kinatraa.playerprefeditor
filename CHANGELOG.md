# Changelog

All notable changes to this package are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-10-08

### Added
- **Tools ▸ kinatraa ▸ Player Pref Editor** window (UI Toolkit) with live search, type filter, alphabetical key list with type badges and a visible/total count.
- Detail panel with a monospace JSON value field, live validation, an unsaved-changes marker and Save, Revert, Copy JSON and Delete.
- Import with validation and an added/changed/unchanged preview before anything is written; Export and Copy All as formatted JSON.
- Key enumeration from the registry on Windows, and a per-project tracked key list (EditorPrefs) on every platform.
- Best-effort type detection (int, float, string, unknown).
- EditMode tests for JSON round-trips, type validation, invalid input and PlayerPrefs read/write.
- Basic Usage sample with an importable JSON document.

[0.1.0]: https://github.com/kinatraa/com.kinatraa.playerprefeditor/releases/tag/0.1.0

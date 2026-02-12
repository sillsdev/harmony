# Custom View Analysis - Moved to languageforge-lexbox

The Custom View model analysis documentation that was previously in this repository has been **moved to the correct repository**: [sillsdev/languageforge-lexbox](https://github.com/sillsdev/languageforge-lexbox).

## Why the Move?

This analysis was initially created in the `sillsdev/harmony` repository by mistake. However:

- The Custom View feature is being implemented in **languageforge-lexbox**
- languageforge-lexbox uses Harmony as a dependency/library
- The analysis should be in the repository where the implementation will occur

## New Location

The documentation is now located in the `docs/` directory of the languageforge-lexbox repository:

- `docs/CUSTOM_VIEWS_README.md` - Navigation and quick start
- `docs/CUSTOM_VIEW_SUMMARY.md` - Executive summary (5-min read)
- `docs/CUSTOM_VIEW_MODEL_ANALYSIS.md` - Detailed analysis with framework context
- `docs/CUSTOM_VIEW_VISUAL.md` - Implementation roadmap and CRDT patterns

## Related Issues

The analysis covers these languageforge-lexbox issues:
- [#1050 - Allow setting default View](https://github.com/sillsdev/languageforge-lexbox/issues/1050)
- [#1049 - Allow creating custom views/layouts](https://github.com/sillsdev/languageforge-lexbox/issues/1049)
- [#1985 - Custom view data model](https://github.com/sillsdev/languageforge-lexbox/issues/1985)

## What Happened to the Old Files?

The files that were in this repository (`CUSTOM_VIEWS_README.md`, `CUSTOM_VIEW_MODEL_ANALYSIS.md`, `CUSTOM_VIEW_SUMMARY.md`, `CUSTOM_VIEW_VISUAL.md`) have been:
1. Copied to the languageforge-lexbox repository
2. Committed in branch `copilot/custom-view-model-analysis`
3. Will be removed from this repository to avoid confusion

## For Developers

If you're looking for the Custom View model documentation, please visit:
- **Repository**: https://github.com/sillsdev/languageforge-lexbox
- **Branch**: `copilot/custom-view-model-analysis`
- **Location**: `docs/` directory

---

*Note: This file serves as a redirect notice and will be removed once the transition is complete.*

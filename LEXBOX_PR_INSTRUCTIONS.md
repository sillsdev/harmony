# Instructions for Completing the languageforge-lexbox PR

## Summary

The Custom View model analysis has been successfully recreated in the correct repository (languageforge-lexbox). The files have been committed locally but need to be pushed to create the PR.

## Current Status

### ✅ Completed in harmony repository
- Removed the 4 documentation files that were created in wrong repo
- Added redirect notice (`CUSTOM_VIEW_ANALYSIS_MOVED.md`)
- Committed and pushed to branch `copilot/review-custom-view-models`

### ✅ Completed in languageforge-lexbox repository (LOCAL)
- Cloned the repository to `/home/runner/work/harmony/languageforge-lexbox`
- Created branch `copilot/custom-view-model-analysis` (based on develop)
- Copied all 4 documentation files to `docs/` directory
- Committed with message: "Add Custom View model analysis documentation"
- **Commit hash: `16d6b126`**

### ⏳ Needs to be done
The languageforge-lexbox branch needs to be pushed to create the PR.

## Files in languageforge-lexbox Branch

Location: `/home/runner/work/harmony/languageforge-lexbox/docs/`

1. **CUSTOM_VIEWS_README.md** (3.3K)
   - Navigation guide
   - Overview of all documentation
   - Quick summary of findings

2. **CUSTOM_VIEW_SUMMARY.md** (4.7K)
   - Executive summary
   - 5-minute read for decision makers
   - Clear categorization of fields
   - Specific recommendations

3. **CUSTOM_VIEW_MODEL_ANALYSIS.md** (12K)
   - Comprehensive detailed analysis
   - Full context from GitHub issues
   - Detailed reasoning for each field
   - Framework requirements
   - Implementation requirements

4. **CUSTOM_VIEW_VISUAL.md** (10K)
   - Visual diagrams of model structure
   - Change class hierarchy
   - Data flow examples
   - CRDT merge scenarios
   - Phased implementation roadmap

## How to Push the Branch

Since the GitHub Actions environment doesn't have push credentials for languageforge-lexbox, the branch needs to be pushed manually or through a different mechanism.

### Option 1: Manual Push (If you have access)
```bash
cd /home/runner/work/harmony/languageforge-lexbox
git push -u origin copilot/custom-view-model-analysis
```

### Option 2: Create PR from Patch
The commit has been saved as a patch file:
```bash
# The patch is at: /tmp/0001-Add-Custom-View-model-analysis-documentation.patch
# Apply it to a local clone of languageforge-lexbox:
git am /tmp/0001-Add-Custom-View-model-analysis-documentation.patch
```

### Option 3: Cherry-pick Commit
If you have access to the local repository:
```bash
# In your local languageforge-lexbox clone:
git fetch origin copilot/custom-view-model-analysis
git cherry-pick 16d6b126
```

## Verification Commands

To verify the files are ready in languageforge-lexbox:
```bash
cd /home/runner/work/harmony/languageforge-lexbox
git log --oneline -3
git show --stat HEAD
ls -lh docs/CUSTOM*.md
```

## PR Details for languageforge-lexbox

**Branch**: `copilot/custom-view-model-analysis`  
**Base**: `develop`  
**Title**: Custom View model analysis: field validation and design recommendations

**Description**:
```
Analysis of Custom View model proposed in issues #1050, #1049, #1985 to identify correct fields, missing requirements, and design decisions needed before implementation.

## Documentation Created

- **CUSTOM_VIEWS_README.md** - Navigation and quick start
- **CUSTOM_VIEW_SUMMARY.md** - Executive summary (5-min read)
- **CUSTOM_VIEW_MODEL_ANALYSIS.md** - Detailed analysis with framework context
- **CUSTOM_VIEW_VISUAL.md** - Implementation roadmap and CRDT patterns

## Field Analysis Results

### ✅ Correct (2/9)
- `Guid Id { get; init; }` - IObjectBase requirement
- `required string Name { get; set; }` - User identification

### ❌ Must Add (1)
- `DateTimeOffset? DeletedAt { get; set; }` - **Mandatory IObjectBase field for soft-delete/CRDT**

### ⚠️ Require Design Decisions (6)
- `DefaultAsOf` → Replace with `bool IsDefault` (fragile timestamp design)
- `DefaultFilter` → Needs specification (format, validation, application point)
- `string[] Fields` → Change to `ViewField[]` for per-field config per @myieye feedback
- `WritingSystemId[] Vernacular/Analysis` → Types undefined; define as record struct
- `ViewBase Base` → Enum undefined; use string for extensibility

### ➕ Consider Adding (3)
- `Description`, `CreatedBy/CreatedAt`, `IsSystemView` for auditing/access control

## Blocking Questions

1. Default mechanism: per-project boolean vs per-role?
2. Filter format and validation requirements?
3. Access control model?

See CUSTOM_VIEW_SUMMARY.md for quick reference or CUSTOM_VIEW_MODEL_ANALYSIS.md for comprehensive details.
```

## Related Issues

- [#1050 - Allow setting default View](https://github.com/sillsdev/languageforge-lexbox/issues/1050)
- [#1049 - Allow creating custom views/layouts](https://github.com/sillsdev/languageforge-lexbox/issues/1049)
- [#1985 - Custom view data model](https://github.com/sillsdev/languageforge-lexbox/issues/1985)

## Notes

- This corrects the mistake of creating the analysis in the harmony repository
- The harmony PR will show the files being removed with a redirect notice
- Both repositories now have appropriate content

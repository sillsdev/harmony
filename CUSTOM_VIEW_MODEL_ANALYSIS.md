# Custom View Model Analysis

## Overview
This document analyzes the proposed CustomView model from issue #1985 to determine which fields are correct, which need decisions, and what should be added for the Harmony CRDT library implementation.

## Proposed Model (from issue #1985)
```csharp
public class CustomView
{
    public Guid Id { get; init; }
    public required string Name { get; set; }
    public DateTimeOffset? DefaultAsOf { get; set; }
    public string? DefaultFilter { get; set; }
    public required string[] Fields { get; set; }
    public required WritingSystemId[] Vernacular { get; set; }
    public required WritingSystemId[] Analysis { get; set; }
    public ViewBase Base { get; set; }
}

public enum ViewBase
{
    FwLite,
    FieldWorks,
}
```

---

## Fields Analysis

### ✅ OBVIOUSLY CORRECT FIELDS

#### 1. `Guid Id { get; init; }`
**Why:** 
- **Required by Harmony framework** - All entities implementing `IObjectBase<T>` must have a `Guid Id { get; init; }` property
- See `Word.cs` line 10, `Definition.cs`, `Example.cs` - all follow this pattern
- The `init` accessor is correct as IDs should not change after creation
- Essential for CRDT operations and entity references

#### 2. `required string Name { get; set; }`
**Why:**
- User-facing identifier for the view (e.g., "Low Literacy View", "Manager View")
- Required to distinguish between multiple custom views in UI
- Mutable because users should be able to rename views
- The `required` keyword ensures views always have names

---

### 🤔 FIELDS NEEDING DECISIONS OR MORE THOUGHT

#### 3. `DateTimeOffset? DefaultAsOf { get; set; }`
**Issues:**
- **Confusing naming**: "DefaultAsOf" doesn't clearly communicate intent
- **Design question**: The issue states "single default Custom View (based on `Max(DefaultAsOf)`)" - but this seems fragile
- **Better alternatives:**
  - `bool IsDefault { get; set; }` with validation ensuring only one view can be default
  - `int DefaultPriority { get; set; }` where highest number wins
  - A separate entity to track the default view per project
- **Consideration**: Comments in issue #1985 suggest defaults per user role, which this field doesn't support

**Recommendation:** 
- Replace with `bool IsDefault { get; set; }` for simplicity
- Consider separate tracking entity if per-role defaults are needed
- The timestamp-based approach could lead to race conditions in CRDT merges

#### 4. `string? DefaultFilter { get; set; }`
**Issues:**
- **Unclear semantics**: What format is the filter? SQL? LINQ? Custom DSL?
- **Scope question**: What does "default filter" mean - applied when view is selected or when project opens?
- **Relationship**: How does this relate to issue #1050's requirement about "setting default view"?
- **Security concern**: If it's SQL or code, could be injection vulnerability

**Needs decisions on:**
- Filter format/syntax specification
- Where/when filters are applied
- Validation and sanitization requirements
- Whether this belongs on the view or should be separate (like search filters)

**Recommendation:** May want to defer this field until filter requirements are better defined

#### 5. `required string[] Fields { get; set; }`
**Issues:**
- **Type suggestion from comment**: Issue comment from @myieye suggests fields should be objects, not strings:
  > "If Fields were an array of objects, they'd be more flexible. E.g. some could be marked readonly."
- **Field identification**: How are fields identified? By name string? By Guid? What about custom fields (issue #1966)?
- **Extensibility**: Array of strings doesn't allow for per-field configuration (visibility, editability, order, width, etc.)

**Better design:**
```csharp
public class ViewField
{
    public required string FieldId { get; set; }  // Name or CustomFieldId reference
    public bool IsReadOnly { get; set; }
    public int DisplayOrder { get; set; }
    // Future: DisplayWidth, IsRequired, etc.
}

public required ViewField[] Fields { get; set; }
```

**Recommendation:** Change to structured ViewField objects for extensibility

#### 6. `required WritingSystemId[] Vernacular { get; set; }`
#### 7. `required WritingSystemId[] Analysis { get; set; }`
**Issues:**
- **Type doesn't exist**: `WritingSystemId` is not defined in Harmony - needs to be created
- **Domain-specific**: These are very specific to linguistic applications (FW Lite)
- **Question**: Should Harmony (as a generic CRDT library) include language-specific types?
- **Alternative**: Could these be generic key-value configuration instead?

**Design decision needed:**
- If Harmony is meant to be generic, consider `Dictionary<string, string[]>` for extensibility
- If Harmony is FW-specific, define `WritingSystemId` properly with validation
- Per issue text: "Custom views will not support per field writing system visibility"

**Recommendation:** 
- Define `WritingSystemId` as a value type (record struct) if keeping linguistic focus
- Consider making this more generic if Harmony aims to support other domains

#### 8. `ViewBase Base { get; set; }`
**Issues:**
- **Type doesn't exist**: `ViewBase` enum not defined in Harmony
- **Limited extensibility**: Only FwLite and FieldWorks - what about WeSay (mentioned in issue #1049)?
- **Purpose unclear**: How does this affect behavior? What do these base views provide?
- **Question**: Is this metadata or does it affect rendering?

**Needs decisions on:**
- Complete list of base views (WeSay mentioned in #1049)
- What "base" actually determines (field labels per the issue)
- Whether this should be extensible (enum vs string)

**Recommendation:**
- Use string instead of enum for extensibility
- Or include all known bases: `FwLite`, `FieldWorks`, `WeSay`, `Custom`

---

### ➕ FIELDS THAT SHOULD BE ADDED

#### 9. `DateTimeOffset? DeletedAt { get; set; }`
**Why:**
- **REQUIRED by Harmony framework** - All `IObjectBase<T>` entities must have this
- See `Word.cs` line 11, all sample models have it
- Enables soft-delete pattern used throughout Harmony
- Critical for CRDT conflict resolution

**Must add this field.**

#### 10. Consider: `string? Description { get; set; }`
**Why:**
- Users may want longer explanation of view purpose
- Helps with discoverability ("Low Literacy View - Simplified for new learners")
- Common in UI configuration entities
- Optional field, low cost

#### 11. Consider: `Guid? CreatedBy { get; set; }` and `DateTimeOffset CreatedAt { get; set; }`
**Why:**
- Useful for auditing who created views
- Could help with permission checking
- Mentioned in issues that project managers create views
- Could support "my views" vs "shared views" feature

#### 12. Consider: `bool IsSystemView { get; set; }`
**Why:**
- Distinguish between built-in views (FW, LF, WeSay) and custom views
- System views shouldn't be deletable/editable
- Relates to the ViewBase concept - maybe system views have a base, custom ones don't?

#### 13. Consider: View visibility/permissions
**Why:**
- Issues mention "project managers to configure, regular users to change views"
- May need `bool IsPublic { get; set; }` or `Guid[] VisibleToRoles { get; set; }`
- Currently model has no access control

---

## Additional Considerations from Issue Comments

### From issue #1985, comment by @myieye:
1. **"We'll presumably keep our current behaviour of locally persisting the currently selected view"**
   - This means CustomView entity tracks available views
   - Current selected view should be local-only (not synced)
   - CustomView entity should sync across all users
   - Need separate mechanism for "current view per user" (local storage, not CRDT)

2. **"If Fields were an array of objects, they'd be more flexible"**
   - Strongly suggests moving away from `string[] Fields`
   - Need structured ViewField type as outlined above

---

## Implementation Requirements for Harmony

To implement CustomView in Harmony CRDT system, we need:

### 1. Core Model
```csharp
public class CustomView : IObjectBase<CustomView>
{
    // Required by IObjectBase
    public Guid Id { get; init; }
    public DateTimeOffset? DeletedAt { get; set; }
    
    // Core view properties
    public required string Name { get; set; }
    public string? Description { get; set; }
    
    // Configuration
    public required ViewField[] Fields { get; set; }
    public required WritingSystemId[] VernacularWritingSystems { get; set; }
    public required WritingSystemId[] AnalysisWritingSystems { get; set; }
    public string ViewBase { get; set; } = "FwLite";
    
    // Default handling - NEEDS DESIGN DECISION
    public bool IsDefault { get; set; }  // Alternative to DefaultAsOf
    
    // Future: DefaultFilter once spec is clear
    
    // Implement IObjectBase methods
    public Guid[] GetReferences() => Array.Empty<Guid>();
    public void RemoveReference(Guid id, CommitBase commit) { }
    public IObjectBase Copy() { /* implementation */ }
}
```

### 2. Supporting Types
```csharp
public record struct WritingSystemId(string Code)
{
    public override string ToString() => Code;
}

public class ViewField
{
    public required string FieldId { get; set; }
    public bool IsReadOnly { get; set; }
    public int DisplayOrder { get; set; }
}
```

### 3. Change Classes
Based on Harmony patterns, we need:
- `CreateCustomViewChange` : CreateChange<CustomView>
- `EditCustomViewChange` : EditChange<CustomView> 
- `DeleteCustomViewChange` : DeleteChange<CustomView>
- `SetViewFieldsChange` : Change<CustomView>
- `SetDefaultViewChange` : Change<CustomView>

### 4. Configuration
Add to CrdtConfig:
```csharp
config.ObjectTypeListBuilder.Add<CustomView>();
config.ChangeTypeListBuilder
    .Add<CreateCustomViewChange>()
    .Add<EditCustomViewChange>()
    .Add<SetViewFieldsChange>()
    .Add<SetDefaultViewChange>();
```

---

## Summary

### Fields Status:
| Field | Status | Action |
|-------|--------|--------|
| `Guid Id` | ✅ Correct | Keep as-is |
| `string Name` | ✅ Correct | Keep as-is |
| `DateTimeOffset? DefaultAsOf` | ⚠️ Needs decision | Replace with `bool IsDefault` or design alternative |
| `string? DefaultFilter` | ⚠️ Needs spec | Defer until requirements clear |
| `string[] Fields` | ⚠️ Should be objects | Change to `ViewField[]` |
| `WritingSystemId[] Vernacular` | ⚠️ Type missing | Define `WritingSystemId` type |
| `WritingSystemId[] Analysis` | ⚠️ Type missing | Define `WritingSystemId` type |
| `ViewBase Base` | ⚠️ Type missing | Define `ViewBase` (prefer string over enum) |
| `DeletedAt` | ❌ Missing | **MUST ADD** - required by Harmony |
| `Description` | ➕ Consider adding | Optional but useful |
| `CreatedBy/CreatedAt` | ➕ Consider adding | Good for auditing |
| `IsSystemView` | ➕ Consider adding | Distinguish built-in vs custom |

### Key Decisions Needed:
1. **Default view mechanism**: Timestamp vs boolean vs separate entity vs per-role
2. **Filter specification**: Format, validation, when applied
3. **Field structure**: String array vs structured objects (recommendation: objects)
4. **Domain specificity**: Keep linguistic types in generic library or abstract them
5. **Access control**: Who can create/edit/delete views

### Next Steps:
1. Get decisions on open questions
2. Implement base model with obvious/required fields
3. Add supporting types (WritingSystemId, ViewField, ViewBase)
4. Create Change classes following Harmony patterns
5. Add tests
6. Document usage in README

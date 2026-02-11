# Custom View Model - Visual Overview

## Current Proposal vs Recommended Changes

### Original Proposal (Issue #1985)
```
CustomView
├── Guid Id
├── string Name
├── DateTimeOffset? DefaultAsOf          ⚠️ Problematic
├── string? DefaultFilter                ⚠️ Needs spec
├── string[] Fields                      ⚠️ Should be objects
├── WritingSystemId[] Vernacular         ⚠️ Type missing
├── WritingSystemId[] Analysis           ⚠️ Type missing
└── ViewBase Base                        ⚠️ Enum missing
```

### Recommended Model
```
CustomView : IObjectBase<CustomView>
├── Guid Id { get; init; }               ✅ Keep (required by framework)
├── DateTimeOffset? DeletedAt            ✅ ADD (required by framework)
├── required string Name                 ✅ Keep (core feature)
├── string? Description                  ➕ Consider adding
│
├── bool IsDefault                       ⚠️ Replace DefaultAsOf
├── ViewField[] Fields                   ⚠️ Replace string[]
│   └── ViewField
│       ├── string FieldId
│       ├── bool IsReadOnly
│       └── int DisplayOrder
│
├── WritingSystemId[] VernacularWS       ⚠️ Define type
├── WritingSystemId[] AnalysisWS         ⚠️ Define type
│   └── WritingSystemId
│       └── record struct(string Code)
│
├── string ViewBase                      ⚠️ Use string not enum
│   (values: "FwLite", "FieldWorks", "WeSay", "Custom")
│
├── Guid? CreatedBy                      ➕ Consider adding
├── DateTimeOffset CreatedAt             ➕ Consider adding
└── bool IsSystemView                    ➕ Consider adding
```

---

## Change Classes (Following Harmony Pattern)

```
Change Classes for CustomView
│
├── CreateCustomViewChange : CreateChange<CustomView>
│   └── Creates new custom view entity
│
├── EditCustomViewChange : EditChange<CustomView>
│   └── General purpose editing
│
├── SetViewFieldsChange : Change<CustomView>
│   ├── Manages Fields array
│   └── Preserves field order as ordered CRDT
│
├── SetDefaultViewChange : Change<CustomView>
│   ├── Sets view as default
│   └── May need to unset other defaults
│
├── SetViewNameChange : Change<CustomView>
│   └── Rename view (specific change for better CRDT merge)
│
└── DeleteCustomViewChange : DeleteChange<CustomView>
    └── Soft delete (sets DeletedAt)
```

---

## Supporting Types Hierarchy

```
Supporting Types
│
├── WritingSystemId (value type)
│   ├── record struct WritingSystemId(string Code)
│   ├── Examples: "en", "es", "fr", "qaa-x-kal"
│   └── Immutable for safety
│
├── ViewField (reference type)
│   ├── required string FieldId
│   │   ├── Standard field: "Definition", "PartOfSpeech"
│   │   └── Custom field: Guid string representation
│   ├── bool IsReadOnly (default: false)
│   ├── int DisplayOrder (determines field sequence)
│   └── Future extensions: Width, IsRequired, etc.
│
└── ViewBase (string, not enum)
    ├── "FwLite" - FW Lite labels
    ├── "FieldWorks" - FieldWorks labels  
    ├── "WeSay" - WeSay labels
    └── "Custom" - User-defined labels
```

---

## Integration with Harmony Framework

```
CrdtConfig Setup
│
services.AddCrdtData<AppDbContext>(config =>
{
    // Register object types
    config.ObjectTypeListBuilder
        .Add<CustomView>();
    
    // Register change types
    config.ChangeTypeListBuilder
        .Add<CreateCustomViewChange>()
        .Add<EditCustomViewChange>()
        .Add<SetViewFieldsChange>()
        .Add<SetDefaultViewChange>()
        .Add<SetViewNameChange>();
});
```

---

## Usage Flow

```
User Actions → Change Objects → CRDT System → Database
│
├── 1. Create View
│   User creates new view
│   → CreateCustomViewChange
│   → CRDT applies change
│   → New CustomView entity in DB
│   → Syncs to all users
│
├── 2. Edit Fields
│   User reorders/configures fields  
│   → SetViewFieldsChange
│   → CRDT merges with other changes
│   → Updated Fields array
│   → Syncs to all users
│
├── 3. Set Default
│   Manager sets default view
│   → SetDefaultViewChange
│   → Unsets other defaults
│   → Sets IsDefault = true
│   → Syncs to all users
│   → Users' current view changes
│
└── 4. Delete View
    User deletes custom view
    → DeleteCustomViewChange  
    → Sets DeletedAt timestamp
    → Soft delete (recoverable)
    → Syncs to all users
```

---

## Data Flow Example

```
Creating a "Low Literacy View"
│
1. Client A: Manager creates view
   ┌────────────────────────────────────┐
   │ CreateCustomViewChange             │
   │ - Id: new Guid()                   │
   │ - Name: "Low Literacy View"        │
   │ - ViewBase: "FwLite"               │
   │ - Fields: [                        │
   │     { FieldId: "Word", Order: 0 }  │
   │     { FieldId: "Definition", ... } │
   │   ]                                │
   └────────────────────────────────────┘
                    ↓
2. DataModel applies change
   ┌────────────────────────────────────┐
   │ CustomView entity created          │
   │ - Stored in Snapshot table         │
   │ - Change in Commit table           │
   │ - ChangeEntity for serialization   │
   └────────────────────────────────────┘
                    ↓
3. Sync to other clients
   ┌──────────┐    ┌──────────┐    ┌──────────┐
   │ Client A │───→│  Server  │───→│ Client B │
   │ (origin) │    │          │    │ Client C │
   └──────────┘    └──────────┘    └──────────┘
                                           ↓
4. All clients now have the view
   - View appears in view selector UI
   - Can be selected by any user
   - If IsDefault=true, becomes active view
```

---

## CRDT Merge Example

```
Concurrent Edits - How CRDT Handles Conflicts

Time T0: CustomView exists with 3 fields
   Fields: [Field1, Field2, Field3]

Time T1: Offline clients make changes
   ┌──────────────────────────────────────┐
   │ Client A (Project Manager)           │
   │ Adds Field4 at end                   │
   │ Fields: [F1, F2, F3, F4]            │
   └──────────────────────────────────────┘
   
   ┌──────────────────────────────────────┐
   │ Client B (Editor)                    │
   │ Removes Field2                       │  
   │ Fields: [F1, F3]                     │
   └──────────────────────────────────────┘

Time T2: Both clients sync
   
   CRDT Merge Algorithm:
   ┌──────────────────────────────────────┐
   │ 1. Compare commit timestamps         │
   │ 2. Apply changes in order            │
   │ 3. Resolve based on change intent    │
   │                                      │
   │ Result: [F1, F3, F4]                │
   │ - F2 removed (DeleteChange wins)    │
   │ - F4 added (CreateChange preserves) │
   └──────────────────────────────────────┘

Key: Each change records intent, not just final state
     This enables intelligent merging
```

---

## Implementation Priority

### Phase 1: Core Model (Must Have)
- ✅ Define CustomView : IObjectBase<CustomView>
- ✅ Add required fields: Id, DeletedAt, Name
- ✅ Define WritingSystemId record struct
- ✅ Define ViewField class
- ✅ Add Fields as ViewField[]
- ✅ Add writing system arrays
- ✅ Add ViewBase string field

### Phase 2: Basic Operations (Should Have)
- ⬜ CreateCustomViewChange
- ⬜ EditCustomViewChange  
- ⬜ DeleteCustomViewChange
- ⬜ Register with CrdtConfig
- ⬜ Unit tests for model
- ⬜ Unit tests for changes

### Phase 3: Default Handling (Should Have)
- ⬜ Add IsDefault field (or design alternative)
- ⬜ SetDefaultViewChange
- ⬜ Logic to ensure single default
- ⬜ Tests for default behavior

### Phase 4: Advanced Features (Nice to Have)
- ⬜ Add Description field
- ⬜ Add audit fields (CreatedBy, CreatedAt)
- ⬜ Add IsSystemView flag
- ⬜ Filter specification and implementation
- ⬜ Access control model
- ⬜ Per-role defaults

---

## Questions for Stakeholders

Before proceeding with implementation:

1. **Default View Mechanism**
   - ❓ Single default per project or per role?
   - ❓ Boolean flag or timestamp or separate table?
   - ❓ What happens when default view is deleted?

2. **Filter Specification**  
   - ❓ What format for DefaultFilter?
   - ❓ Where/when are filters applied?
   - ❓ Security/validation requirements?

3. **Access Control**
   - ❓ Can all users create views?
   - ❓ Can users delete others' views?
   - ❓ Role-based permissions needed?

4. **Field Configuration**
   - ❓ ViewField properties sufficient?
   - ❓ Need per-field WS visibility?
   - ❓ Support for field-level permissions?

5. **Harmony Scope**
   - ❓ Keep FW-specific types in generic library?
   - ❓ Or make CustomView example in Sample project?
   - ❓ Target: Harmony.Core or Harmony.Sample?

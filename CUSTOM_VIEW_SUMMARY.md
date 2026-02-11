# Custom View Model - Quick Summary

## Purpose
Design a CRDT-compatible CustomView model for Harmony based on languageforge-lexbox issues #1050, #1049, and #1985.

---

## ✅ Fields That Are Obviously Correct

### 1. `Guid Id { get; init; }`
**Why correct:** Required by Harmony's `IObjectBase<T>` interface. All entities in Harmony must have this property with `init` accessor.

### 2. `required string Name { get; set; }`
**Why correct:** Essential for users to identify and distinguish between different custom views in the UI. The `required` modifier ensures every view has a name, and `set` allows renaming.

---

## ⚠️ Fields That Need Decisions or More Thought

### 3. `DateTimeOffset? DefaultAsOf { get; set; }`
**Problems:**
- Unclear naming - doesn't communicate purpose well
- Using `Max(DefaultAsOf)` to determine default view is fragile and could race in CRDT merges
- Doesn't support per-role defaults mentioned in issue comments

**Recommendation:** Replace with `bool IsDefault { get; set; }` with validation, or design a separate entity for default view tracking.

---

### 4. `string? DefaultFilter { get; set; }`
**Problems:**
- No specification for filter format (SQL? LINQ? Custom?)
- Unclear when/where filters apply
- Potential security concerns if not properly validated

**Recommendation:** Defer implementation until requirements are better defined.

---

### 5. `required string[] Fields { get; set; }`
**Problems:**
- Issue comment from @myieye specifically requests objects instead of strings for flexibility (e.g., readonly fields)
- Cannot support per-field configuration (visibility, editability, order, width)
- How are custom fields (issue #1966) identified?

**Recommendation:** Use structured objects:
```csharp
public class ViewField
{
    public required string FieldId { get; set; }
    public bool IsReadOnly { get; set; }
    public int DisplayOrder { get; set; }
}

public required ViewField[] Fields { get; set; }
```

---

### 6-7. `required WritingSystemId[] Vernacular/Analysis { get; set; }`
**Problems:**
- `WritingSystemId` type doesn't exist in Harmony - must be defined
- Very domain-specific for a generic CRDT library
- Need to decide if Harmony should include linguistic types or stay generic

**Recommendation:** Define as value type: `public record struct WritingSystemId(string Code);`

---

### 8. `ViewBase Base { get; set; }`
**Problems:**
- `ViewBase` enum doesn't exist in Harmony
- Only includes FwLite and FieldWorks, but WeSay mentioned in issue #1049
- Limited extensibility with enum

**Recommendation:** Use `string ViewBase { get; set; } = "FwLite";` for better extensibility.

---

## ❌ Fields That MUST Be Added

### 9. `DateTimeOffset? DeletedAt { get; set; }`
**Why required:** This is a **mandatory** field for all entities implementing `IObjectBase<T>` in Harmony. Used for soft-delete pattern and CRDT conflict resolution.

**Must add without question.**

---

## ➕ Fields to Consider Adding

### 10. `string? Description { get; set; }`
Useful for explaining view purpose to users (e.g., "Low Literacy View - Simplified interface for new learners").

### 11. `Guid? CreatedBy { get; set; }` and `DateTimeOffset CreatedAt { get; set; }`
Helpful for auditing, tracking view ownership, and potentially implementing "my views" vs "shared views".

### 12. `bool IsSystemView { get; set; }`
Distinguish between built-in views (FW, LF, WeSay) and user-created custom views. System views should not be deletable.

---

## Implementation Checklist

To properly implement CustomView in Harmony:

- [ ] Define `CustomView` class implementing `IObjectBase<CustomView>`
- [ ] Include mandatory `DeletedAt` field
- [ ] Define `WritingSystemId` record struct
- [ ] Define `ViewField` class (instead of string array)
- [ ] Define `ViewBase` (recommend string instead of enum)
- [ ] Create `CreateCustomViewChange` : CreateChange<CustomView>
- [ ] Create `EditCustomViewChange` : EditChange<CustomView>
- [ ] Create `SetViewFieldsChange` : Change<CustomView>
- [ ] Register types in `CrdtConfig`
- [ ] Add unit tests
- [ ] Update documentation

---

## Key Open Questions

Before implementation, need decisions on:

1. **Default view mechanism:** Boolean flag vs timestamp vs separate tracking entity vs per-role defaults?
2. **Filter specification:** What format? When applied? How validated?
3. **Access control:** Who can create/edit/delete views?
4. **Domain specificity:** Should Harmony include FW-specific types or stay generic?

---

## References

- **Issue #1050:** Allow setting default View
- **Issue #1049:** Allow creating custom views/layouts  
- **Issue #1985:** Custom view data model (with proposed model)
- **Harmony Patterns:** See `src/SIL.Harmony.Sample/Models/Word.cs` and related Change classes

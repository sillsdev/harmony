# Custom Views - Start Here

This directory contains analysis documentation for implementing Custom Views in the Harmony CRDT library.

## 📚 Documentation Files

### 1. **CUSTOM_VIEW_SUMMARY.md** ⭐ START HERE
Quick reference guide for decision makers and busy stakeholders.
- 2-minute read
- Clear categorization of all fields
- Specific recommendations
- Implementation checklist

### 2. **CUSTOM_VIEW_MODEL_ANALYSIS.md**
Comprehensive detailed analysis with full reasoning.
- Complete context from all 3 GitHub issues
- Detailed explanations for each decision
- Framework requirements documented
- Reference for implementers

### 3. **CUSTOM_VIEW_VISUAL.md**
Visual diagrams and implementation roadmap.
- Model structure diagrams
- Change class hierarchy
- Data flow examples
- CRDT merge scenarios
- Phased implementation plan

## 🎯 Quick Summary

Based on issues [#1050](https://github.com/sillsdev/languageforge-lexbox/issues/1050), [#1049](https://github.com/sillsdev/languageforge-lexbox/issues/1049), and [#1985](https://github.com/sillsdev/languageforge-lexbox/issues/1985) from languageforge-lexbox:

### Fields Status
- ✅ **2 fields are obviously correct**: Id, Name
- ⚠️ **6 fields need decisions**: DefaultAsOf, DefaultFilter, Fields, Vernacular, Analysis, Base
- ❌ **1 field MUST be added**: DeletedAt (framework requirement)
- ➕ **3 fields to consider**: Description, CreatedBy/CreatedAt, IsSystemView

### Key Finding
The proposed model in issue #1985 is a good starting point but needs refinement:
1. Missing mandatory `DeletedAt` field
2. Several supporting types are undefined
3. `string[] Fields` should be `ViewField[]` objects (per @myieye's feedback)
4. Default mechanism needs design decision

## 🚀 Next Steps

### For Decision Makers
1. Read **CUSTOM_VIEW_SUMMARY.md** (5 minutes)
2. Make decisions on the 4 key questions at the end
3. Review **CUSTOM_VIEW_VISUAL.md** for implementation phases

### For Implementers
1. Read all three documents in order
2. Wait for stakeholder decisions on open questions
3. Follow the phased implementation plan in CUSTOM_VIEW_VISUAL.md

## ❓ Key Questions Requiring Decisions

Before implementation can proceed:

1. **Default view**: Boolean flag, timestamp, or per-role mechanism?
2. **Filter specification**: What format? When applied?
3. **Access control**: Who can manage views?
4. **Library scope**: Generic Harmony.Core or FW-specific Harmony.Sample?

## 📝 Context

**What**: Design CRDT-compatible CustomView model for Harmony  
**Why**: Enable users to configure field visibility and writing systems  
**Where**: Harmony repository (this repo), used by languageforge-lexbox  
**When**: Foundation for vNext milestone features  

## 🔗 Related Issues

- [#1050 - Allow setting default View](https://github.com/sillsdev/languageforge-lexbox/issues/1050)
- [#1049 - Allow creating custom views/layouts](https://github.com/sillsdev/languageforge-lexbox/issues/1049)
- [#1985 - Custom view data model](https://github.com/sillsdev/languageforge-lexbox/issues/1985)
- [#1966 - Custom fields](https://github.com/sillsdev/languageforge-lexbox/issues/1966) (referenced)

---

## Branch Information

**Branch**: `copilot/review-custom-view-models`  
**Base**: Latest from main repository  
**Status**: Analysis complete, awaiting stakeholder decisions  

Created: 2026-02-11

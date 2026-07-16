using SIL.Harmony.Refs.Changes;
using SIL.Harmony.Refs.Entities;

namespace SIL.Harmony.Refs;

public static class HarmonyRefsKernel
{
    /// <summary>
    /// Registers branch (and later tag) ref entities and change types on an existing Harmony <see cref="CrdtConfig"/>.
    /// </summary>
    public static CrdtConfig AddHarmonyRefs(this CrdtConfig config)
    {
        config.ObjectTypeListBuilder.DefaultAdapter().Add<Branch>();
        config.ChangeTypeListBuilder.Add<CreateBranchChange>();
        return config;
    }
}

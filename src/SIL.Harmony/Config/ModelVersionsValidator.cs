using Microsoft.Extensions.Options;

namespace SIL.Harmony.Config;

/// <summary>
/// Model versions are required, since they register the change types.
/// Harmony's own change types must be listed through <see cref="ModelVersionChanges.RemoteResourceChanges{TMetadata}"/>,
/// so a Harmony version can be slotted in there later, see https://github.com/sillsdev/harmony/issues/141.
/// </summary>
internal class ModelVersionsValidator : IValidateOptions<HarmonyConfig>
{
    public ValidateOptionsResult Validate(string? name, HarmonyConfig options)
    {
        if (options.ModelVersions.Count == 0)
            return ValidateOptionsResult.Fail(
                $"Declare model versions with {nameof(HarmonyConfig)}.{nameof(HarmonyConfig.ModelVersionBuilder)}, " +
                "starting with version 0 listing every change type that existed before model versions");
        var remoteResourcesMetadataType = options.RemoteResourceMetadataType;
        var remoteResourceChangesMetadataType = options.ModelVersionBuilder.RemoteResourceChangesMetadataType;
        if (remoteResourcesMetadataType != remoteResourceChangesMetadataType)
            return ValidateOptionsResult.Fail(
                $"AddCrdtRemoteResources and {nameof(ModelVersionChanges.RemoteResourceChanges)} in a model version must both be called, " +
                $"with the same metadata type, or neither: got {remoteResourcesMetadataType?.Name ?? "not called"} " +
                $"and {remoteResourceChangesMetadataType?.Name ?? "not called"}");
        return ValidateOptionsResult.Success;
    }
}

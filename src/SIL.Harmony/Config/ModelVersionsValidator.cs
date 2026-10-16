using Microsoft.Extensions.Options;

namespace SIL.Harmony.Config;

/// <summary>
/// Model versions are required, and every registered change type must be listed by one,
/// so a new change type can't be released without a version that makes old apps replay it.
/// </summary>
internal class ModelVersionsValidator : IValidateOptions<HarmonyConfig>
{
    public ValidateOptionsResult Validate(string? name, HarmonyConfig options)
    {
        if (options.ModelVersions.Count == 0)
            return ValidateOptionsResult.Fail(
                $"Declare model versions with {nameof(HarmonyConfig)}.{nameof(HarmonyConfig.ModelVersionBuilder)}, " +
                "starting with version 0 listing every change type that existed before model versions");
        var listed = options.ModelVersionBuilder.ChangeVersions;
        var unlisted = options.ChangeTypes
            .Select(ct => ct.Discriminator)
            .Where(discriminator => !listed.ContainsKey(discriminator))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unlisted.Length > 0)
            return ValidateOptionsResult.Fail(
                $"These change types aren't listed by a model version: {string.Join(", ", unlisted)}. " +
                "Add a new model version that lists them, released versions must not change");
        return ValidateOptionsResult.Success;
    }
}

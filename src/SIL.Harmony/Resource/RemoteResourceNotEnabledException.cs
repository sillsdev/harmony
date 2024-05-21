namespace SIL.Harmony.Resource;

public class RemoteResourceNotEnabledException()
    : Exception("remote recources were not enabled, to enable them call CrdtConfig.AddRemoteResourceEntity when adding the CRDT library");

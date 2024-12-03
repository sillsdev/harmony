namespace SIL.Harmony.Resource;

public class RemoteResourceNotEnabledException()
    : Exception("remote resources were not enabled, to enable them call CrdtConfig.AddRemoteResourceEntity when adding the CRDT library");

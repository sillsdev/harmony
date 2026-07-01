namespace SIL.Harmony.Resource;

public class RemoteResourceNotEnabledException()
    : Exception("remote resources were not enabled, to enable them call AddCrdtRemoteResources<TMetadata> when adding the CRDT library");

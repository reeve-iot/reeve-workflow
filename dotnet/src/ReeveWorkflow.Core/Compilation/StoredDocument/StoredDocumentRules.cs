namespace ReeveWorkflow.Core.Compilation;

internal static class StoredDocumentRules
{
    public const string L1 = "L1";

    public const string L2 = "L2";

    public const string L3 = "L3";

    public const string L4 = "L4";

    public const string L5 = "L5";

    public const string L7 = "L7";

    public const string DuplicateNodeId = "duplicate-node-id";

    public const string DuplicateEdgeId = "duplicate-edge-id";
    public const string OutputMappingsUnread = "output-mappings-unread";

    public const string UnknownKey = "stored-key-unknown";

    public const string DottedName = "dotted-name";

    public const string ErrorPolicyRouteUnread = "error-policy-route-unread";

    public static IReadOnlyList<string> All { get; } = [L1, L2, L3, L4, L5, L7, DuplicateNodeId, DuplicateEdgeId, OutputMappingsUnread, UnknownKey, DottedName, ErrorPolicyRouteUnread];
}

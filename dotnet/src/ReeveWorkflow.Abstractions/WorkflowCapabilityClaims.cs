using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;

namespace ReeveWorkflow.Abstractions;

internal static class WorkflowCapabilityClaims
{
    internal const DynamicallyAccessedMemberTypes ClaimedMembers =
        DynamicallyAccessedMemberTypes.Interfaces | DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods;

    public static IEnumerable<WorkflowRegistrationRefusal> Verify(
        string subject, string? name, [DynamicallyAccessedMembers(ClaimedMembers)] Type? clrType, IReadOnlySet<WorkflowCapability>? claims, JsonTypeInfo? jsonTypeInfo)
    {
        var refusals = new List<WorkflowRegistrationRefusal>();
        foreach (var claim in (claims ?? new HashSet<WorkflowCapability>()).OrderBy(c => c))
        {
            if (!Enum.IsDefined(claim))
            {
                Refuse(WorkflowRegistrationCodes.CapabilityClaimInvalid, $"it claims '{claim}', which is not a capability");
                continue;
            }

            var capability = PortCapabilities.NameOf(claim);
            if (!PortCapabilities.HostMayClaim(claim))
            {
                Refuse(WorkflowRegistrationCodes.CapabilityClaimInvalid, claim == WorkflowCapability.Iterable
                    ? $"{capability} is the library's: only Collection implements it"
                    : $"{capability} is the library's: only Int and Number implement it");
                continue;
            }

            if (clrType is null)
                continue;
            switch (claim)
            {
                case WorkflowCapability.Comparable when !Implements(clrType, typeof(IComparable<>)):
                    Refuse(WorkflowRegistrationCodes.CapabilityClaimInvalid,
                        $"{capability} claimed, but {clrType.Name} does not implement IComparable<{clrType.Name}>");
                    break;
                case WorkflowCapability.Equatable when !Implements(clrType, typeof(IEquatable<>)) && !IsRecord(clrType):
                    Refuse(WorkflowRegistrationCodes.CapabilityClaimInvalid,
                        $"{capability} claimed, but {clrType.Name} is not a record and does not implement IEquatable<{clrType.Name}>");
                    break;
                case WorkflowCapability.JsonSerializable:
                    if (jsonTypeInfo is null)
                        Refuse(WorkflowRegistrationCodes.PortTypeRegistrationInvalid, $"{nameof(WorkflowCapability.JsonSerializable)} claimed, but the record has no JsonTypeInfo");
                    break;
            }
        }

        return refusals;

        void Refuse(string code, string why) => refusals.Add(new WorkflowRegistrationRefusal(
            code, name ?? "", $"{subject} '{name}' cannot be registered: {why}."));
    }

    private static bool Implements([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type, Type definition) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition && i.GetGenericArguments()[0] == type);

    private static bool IsRecord([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null
        || (type.IsValueType && type.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance) is not null);
}

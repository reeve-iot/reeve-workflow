using System.Reflection;
using Xunit.Sdk;

namespace Dispatch.Host.Tests;

[AttributeUsage(AttributeTargets.Class)]
public sealed class DropTestDatabasesAttribute : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest) => ClusterBackends.DropCreatedDatabases();
}

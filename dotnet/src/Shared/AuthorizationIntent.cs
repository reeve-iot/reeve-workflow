using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Http;

internal static class AuthorizationIntent
{
    internal static void Require(IEndpointRouteBuilder endpoints, RouteGroupBuilder group, string api, string mapMethod)
    {
        ((IEndpointConventionBuilder)group).Finally(builder =>
        {
            if (builder.Metadata.Any(m => m is IAuthorizeData or IAllowAnonymous or AuthorizationPolicy
                    or IAuthorizationRequirementData))
                return;

            throw new InvalidOperationException(
                $"The {api} was mapped without an authorisation intent ({builder.DisplayName}). "
                + $"Call .RequireAuthorization(\"<policy>\") on the group {mapMethod} returns, or "
                + ".AllowAnonymous() to state that an open surface is what this host means.");
        });

        endpoints.ServiceProvider.GetService<WorkflowStartupChecks>()?.Add(
            "endpoints", services => _ = services.GetService<EndpointDataSource>()?.Endpoints);
    }
}

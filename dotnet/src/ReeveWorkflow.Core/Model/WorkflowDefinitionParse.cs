using System.Text.Json;

namespace ReeveWorkflow.Core.Model;

internal static class WorkflowDefinitionParse
{
    internal static WorkflowDefinition? FromText(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowDefinition>(text, WorkflowGraphHelpers.JsonOptions);
        }
        catch (WorkflowValidationException ex)
        {
            throw Unreadable(ex);
        }
    }

    internal static WorkflowDefinition? FromElement(JsonElement element)
    {
        try
        {
            return element.Deserialize<WorkflowDefinition>(WorkflowGraphHelpers.JsonOptions);
        }
        catch (WorkflowValidationException ex)
        {
            throw Unreadable(ex);
        }
    }

    private static JsonException Unreadable(WorkflowValidationException ex) =>
        new(ex.Message, null, null, null, ex);
}

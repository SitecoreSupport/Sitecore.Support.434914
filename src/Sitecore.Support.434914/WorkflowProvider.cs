namespace Sitecore.Support.Workflows.Simple
{
  using Sitecore.Workflows;
  using Sitecore.Workflows.Simple;
  using System;

  public class WorkflowProvider : Sitecore.Workflows.Simple.WorkflowProvider
  {
    public WorkflowProvider(string databaseName, HistoryStore historyStore) : base(databaseName, historyStore)
    {
    }

    protected override IWorkflow InstantiateWorkflow(string workflowId, Sitecore.Workflows.Simple.WorkflowProvider owner) =>
        new Sitecore.Support.Workflows.Simple.Workflow(workflowId, owner);
  }
}

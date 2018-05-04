namespace Sitecore.Workflows.Simple
{
  using Sitecore;
  using Sitecore.Configuration;
  using Sitecore.Data;
  using Sitecore.Data.Items;
  using Sitecore.Diagnostics;
  using Sitecore.Workflows;
  using System;

  public class WorkflowProvider : IWorkflowProvider
  {
    private Sitecore.Data.Database _database;
    private readonly string _databaseName;
    private readonly Sitecore.Workflows.HistoryStore _store;

    public WorkflowProvider(string databaseName, Sitecore.Workflows.HistoryStore historyStore)
    {
      Assert.ArgumentNotNullOrEmpty(databaseName, "databaseName");
      Assert.ArgumentNotNull(historyStore, "historyStore");
      this._databaseName = databaseName;
      this._store = historyStore;
    }

    public virtual IWorkflow GetWorkflow(Item item)
    {
      Assert.ArgumentNotNull(item, "item");
      string workflowID = GetWorkflowID(item);
      if (workflowID.Length > 0)
      {
        return this.InstantiateWorkflow(workflowID, this);
      }
      return null;
    }

    public virtual IWorkflow GetWorkflow(string workflowID)
    {
      Assert.ArgumentNotNullOrEmpty(workflowID, "workflowID");
      Error.Assert(ID.IsID(workflowID), "The parameter 'workflowID' must be parseable to an ID");
      if (this.Database.Items[ID.Parse(workflowID)] != null)
      {
        return this.InstantiateWorkflow(workflowID, this);
      }
      return null;
    }

    protected static string GetWorkflowID(Item item)
    {
      Assert.ArgumentNotNull(item, "item");
      WorkflowInfo workflowInfo = item.Database.DataManager.GetWorkflowInfo(item);
      if (workflowInfo != null)
      {
        return workflowInfo.WorkflowID;
      }
      return string.Empty;
    }

    public virtual IWorkflow[] GetWorkflows()
    {
      Item item = this.Database.Items[ItemIDs.WorkflowRoot];
      if (item == null)
      {
        return new IWorkflow[0];
      }
      Item[] itemArray = item.Children.ToArray();
      IWorkflow[] workflowArray = new IWorkflow[itemArray.Length];
      for (int i = 0; i < itemArray.Length; i++)
      {
        workflowArray[i] = this.InstantiateWorkflow(itemArray[i].ID.ToString(), this);
      }
      return workflowArray;
    }

    public virtual void Initialize(Item configItem)
    {
    }

    protected virtual IWorkflow InstantiateWorkflow(string workflowId, WorkflowProvider owner) =>
        new Workflow(workflowId, owner);

    public virtual Sitecore.Data.Database Database
    {
      get
      {
        if (this._database == null)
        {
          this._database = Factory.GetDatabase(this._databaseName);
        }
        return this._database;
      }
    }

    public virtual Sitecore.Workflows.HistoryStore HistoryStore =>
        this._store;
  }
}

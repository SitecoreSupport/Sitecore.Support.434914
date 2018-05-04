namespace Sitecore.Workflows.Simple
{
  using Sitecore;
  using Sitecore.Collections;
  using Sitecore.Configuration;
  using Sitecore.Data;
  using Sitecore.Data.Fields;
  using Sitecore.Data.Items;
  using Sitecore.Data.Managers;
  using Sitecore.Data.Templates;
  using Sitecore.Diagnostics;
  using Sitecore.Diagnostics.PerformanceCounters;
  using Sitecore.Exceptions;
  using Sitecore.Globalization;
  using Sitecore.Pipelines;
  using Sitecore.Reflection;
  using Sitecore.Security.AccessControl;
  using Sitecore.Security.Accounts;
  using Sitecore.SecurityModel;
  using Sitecore.StringExtensions;
  using Sitecore.Web;
  using Sitecore.Workflows;
  using System;
  using System.Collections;
  using System.Collections.Generic;
  using System.Linq;
  using System.Runtime.InteropServices;
  using System.Runtime.Serialization;
  using System.Web;

  [Serializable]
  public class Workflow : IWorkflow, IAsyncWorkflow, ISerializable
  {
    [NonSerialized]
    private readonly WorkflowProvider _owner;
    [NonSerialized]
    private readonly ID _workflowID;

    public Workflow(SerializationInfo info, StreamingContext context)
    {
      this._workflowID = (ID)info.GetValue("workflowid", typeof(ID));
      string name = (string)info.GetValue("database", typeof(string));
      Sitecore.Data.Database database = Factory.GetDatabase(name);
      this._owner = (WorkflowProvider)database.WorkflowProvider;
    }

    public Workflow(string workflowID, WorkflowProvider owner)
    {
      Assert.ArgumentNotNullOrEmpty(workflowID, "workflowID");
      Assert.ArgumentNotNull(owner, "owner");
      Assert.IsTrue(ID.IsID(workflowID), "workflowID must be parseable to an ID");
      this._workflowID = ID.Parse(workflowID);
      this._owner = owner;
    }

    private void AddHistory(Item item, string oldState, string newState, StringDictionary commentFields)
    {
      this.HistoryStore.AddHistory(item, oldState, newState, commentFields);
    }

    private void ClearHistory(Item item)
    {
      this.HistoryStore.ClearHistory(item);
    }

    protected void CommandActionsComplete(WorkflowPipelineArgs args)
    {
      Assert.ArgumentNotNull(args, "args");
      if (!args.Aborted && !args.CancelTransition)
      {
        Item commandItem = args.CommandItem;
        ID stateId = args.NextStateId ?? this.GetNextStateId(commandItem);
        if (stateId.IsNull)
        {
          throw new WorkflowStateMissingException("No next state could be found for command: " + commandItem.Paths.FullPath);
        }
        if (stateId.ToString() != args.PreviousState.StateID)
        {
          this.PerformTransition(commandItem, args.DataItem, stateId, args.CommentFields);
          args.DataItem.Locking.Unlock();
          Item stateItem = this.GetStateItem(stateId);
          Log.Audit(this, "Execute workflow command. Item: {0}, command: {1}, previous state: {2}, next state: {3}, user: {4}", new string[] { AuditFormatter.FormatItem(args.DataItem), commandItem.Paths.Path, args.PreviousState.DisplayName, (stateItem != null) ? stateItem.Paths.Path : stateId.ToString(), Context.User.Name });
          if (stateItem != null)
          {
            this.ExecuteStateActions(stateItem, args.DataItem, args.CommentFields, args.Parameters);
            if (args.WaitHandle != null)
            {
              args.WaitHandle.Set();
            }
            if (args.CompletionCallback != null)
            {
              args.CompletionCallback();
            }
          }
        }
      }
    }

    private WorkflowState CreateWorkflowState(Item item) =>
        new WorkflowState(item.ID.ToString(), item.DisplayName, item.Appearance.Icon, item[WorkflowFieldIDs.FinalState] == "1", this.GetStatePreviewPublishingTargets(item));

    public virtual WorkflowResult Execute(string commandID, Item item, StringDictionary commentFields, bool allowUI, params object[] parameters) =>
        this.Execute(commandID, item, commentFields, allowUI, null, parameters);

    public virtual WorkflowResult Execute(string commandID, Item item, string comments, bool allowUI, params object[] parameters)
    {
      StringDictionary commentFields = new StringDictionary();
      commentFields.Add("Comments", comments);
      return this.Execute(commandID, item, commentFields, allowUI, null, parameters);
    }

    public virtual WorkflowResult Execute(string commandID, Item item, StringDictionary commentFields, bool allowUI, Processor callback, params object[] parameters)
    {
      Assert.ArgumentNotNullOrEmpty(commandID, "commandID");
      Assert.ArgumentNotNull(item, "item");
      Assert.ArgumentNotNull(commentFields, "commentFields");
      Assert.ArgumentNotNull(parameters, "parameters");
      Item commandItem = this.GetCommandItem(commandID, item);
      if (commandItem == null)
      {
        return new WorkflowResult(false, "Could not find command definition: " + commandID);
      }
      return this.ExecuteCommandActionsAndTransition(commandItem, item, commentFields, parameters, callback);
    }

    public virtual WorkflowResult Execute(string commandID, Item item, string comments, bool allowUI, Processor callback, params object[] parameters)
    {
      StringDictionary commentFields = new StringDictionary();
      commentFields.Add("Comments", comments);
      return this.Execute(commandID, item, commentFields, allowUI, callback, parameters);
    }

    private WorkflowResult ExecuteCommandActionsAndTransition(Item commandItem, Item dataItem, StringDictionary commentFields, object[] parameters, Processor callback = null)
    {
      Pipeline pipeline = null;
      if (commandItem.HasChildren)
      {
        pipeline = PipelineFactory.GetPipeline(commandItem);
        if (pipeline != null)
        {
          pipeline = new Pipeline(pipeline.Name, new ArrayList(pipeline.Processors), Pipeline.PipelineType.Dynamic);
          DataCount.WorkflowActionsExecuted.IncrementBy((long)pipeline.Processors.Count);
        }
      }
      if (pipeline == null)
      {
        pipeline = new Pipeline("Workflow pipeline", new ArrayList(), Pipeline.PipelineType.Dynamic);
      }
      pipeline.Add(new Processor("Workflow state transition", this, "CommandActionsComplete"));
      if (callback != null)
      {
        pipeline.Add(callback);
      }
      WorkflowState workflowState = dataItem.State.GetWorkflowState();
      WorkflowPipelineArgs args = new WorkflowPipelineArgs(commandItem, dataItem, commentFields, parameters, pipeline, workflowState, null, null);
      pipeline.Start(args);
      return new WorkflowResult(!args.Aborted && !args.Suspended, args.Message, args.NextStateId, !args.Suspended);
    }

    private void ExecuteStateActions(Item stateItem, Item dataItem, StringDictionary commentFields, object[] parameters)
    {
      if (stateItem.HasChildren)
      {
        WorkflowPipelineArgs args = new WorkflowPipelineArgs(dataItem, commentFields, parameters);
        Pipeline pipeline = Pipeline.Start(stateItem, args);
        if (pipeline != null)
        {
          DataCount.WorkflowActionsExecuted.IncrementBy((long)pipeline.Processors.Count);
        }
      }
    }

    protected virtual WorkflowResult ExecuteStateTransition(ID nextStateId, Item commandItem, WorkflowState previousState, Item item, StringDictionary commentFields, params object[] parameters)
    {
      ID stateId = nextStateId ?? this.GetNextStateId(commandItem);
      if (stateId.IsNull)
      {
        throw new WorkflowStateMissingException("No next state could be found for command: " + commandItem.Paths.FullPath);
      }
      if (stateId.ToString() != previousState.StateID)
      {
        this.PerformTransition(commandItem, item, stateId, commentFields);
        item.Locking.Unlock();
        Item stateItem = this.GetStateItem(stateId);
        Log.Audit(this, "Execute workflow command. Item: {0}, command: {1}, previous state: {2}, next state: {3}, user: {4}", new string[] { AuditFormatter.FormatItem(item), commandItem.Paths.Path, previousState.DisplayName, (stateItem != null) ? stateItem.Paths.Path : stateId.ToString(), Context.User.Name });
        if (stateItem == null)
        {
          return new WorkflowResult(true);
        }
        this.ExecuteStateActions(stateItem, item, commentFields, parameters);
      }
      return new WorkflowResult(true);
    }

    public AccessResult GetAccess(Item item, Account account, AccessRight accessRight)
    {
      Assert.ArgumentNotNull(item, "item");
      Assert.ArgumentNotNull(account, "account");
      Assert.ArgumentNotNull(accessRight, "operation");
      DataCount.WorkflowSecurityResolved.Increment(1L);
      if (accessRight == AccessRight.ItemDelete)
      {
        return this.GetDeleteAccessInformation(item, account);
      }
      if (accessRight == AccessRight.ItemRemoveVersion)
      {
        return this.GetDeleteVersionAccessInformation(item, account);
      }
      Item stateItem = this.GetStateItem(item);
      if (stateItem == null)
      {
        return new AccessResult(AccessPermission.Allow, new AccessExplanation(item, account, AccessRight.ItemDelete, "The workflow state definition item not found.", new object[0]));
      }
      if (accessRight == AccessRight.ItemWrite)
      {
        return this.GetWriteAccessInformation(item, account, stateItem);
      }
      return new AccessResult(AccessPermission.Allow, new AccessExplanation(item, account, accessRight, "Sitecore only tests workflow state definition item security settings for delete and write access rights.  All other item access rights are based on the item's security settings.", new object[0]));
    }

    protected virtual Item GetCommandItem(string commandID, Item item)
    {
      Assert.ArgumentNotNull(commandID, "commandID");
      Assert.ArgumentNotNull(item, "item");
      Assert.IsTrue(ID.IsID(commandID), "Invalid command ID: " + commandID);
      Item stateItem = this.GetStateItem(item);
      if (stateItem == null)
      {
        return null;
      }
      return stateItem.Axes.GetChild(ID.Parse(commandID));
    }

    public virtual WorkflowCommand[] GetCommands(Item item)
    {
      Assert.ArgumentNotNull(item, "item");
      string stateID = this.GetStateID(item);
      if (stateID.Length > 0)
      {
        return this.GetCommands(stateID, item);
      }
      return new WorkflowCommand[0];
    }

    public virtual WorkflowCommand[] GetCommands(string stateID) =>
        this.GetCommands(stateID, null);

    public virtual WorkflowCommand[] GetCommands(string stateID, Item item)
    {
      Assert.ArgumentNotNullOrEmpty(stateID, "stateID");
      Item stateItem = this.GetStateItem(stateID);
      if (stateItem == null)
      {
        return new WorkflowCommand[0];
      }
      Item[] itemArray = stateItem.Children.ToArray();
      ArrayList list = new ArrayList();
      for (int i = 0; i < itemArray.Length; i++)
      {
        Item entity = itemArray[i];
        if (entity != null)
        {
          Template template = entity.Database.Engines.TemplateEngine.GetTemplate(entity.TemplateID);
          if (((template != null) && template.DescendsFromOrEquals(TemplateIDs.WorkflowCommand)) && AuthorizationManager.IsAllowed(entity, AccessRight.WorkflowCommandExecute, Context.User))
          {
            string displayName = entity.DisplayName;
            string icon = entity.Appearance.Icon;
            bool hasUI = false;
            bool suppressComment = false;
            if (!string.IsNullOrEmpty(entity[FieldIDs.AppearanceEvaluatorType]))
            {
              IWorkflowCommandAppearanceEvaluator evaluator = ReflectionUtil.CreateObject(entity[FieldIDs.AppearanceEvaluatorType]) as IWorkflowCommandAppearanceEvaluator;
              if (evaluator != null)
              {
                displayName = evaluator.GetCommandName(item, entity);
                icon = evaluator.GetCommandIcon(item, entity);
                hasUI = evaluator.HasUI(item, entity);
                suppressComment = evaluator.SuppressComments(item, entity);
              }
              else
              {
                Log.Error(Translate.Text("Type \"{0}\" not found.").FormatWith(new object[] { entity[FieldIDs.AppearanceEvaluatorType] }), this);
              }
            }
            list.Add(new WorkflowCommand(entity.ID.ToString(), displayName, icon, hasUI, suppressComment));
          }
        }
      }
      return (WorkflowCommand[])list.ToArray(typeof(WorkflowCommand));
    }

    private AccessResult GetDeleteAccessInformation(Item item, Account account)
    {
      bool flag = true;
      foreach (Item item2 in item.Versions.GetVersions(true))
      {
        Item stateItem = this.GetStateItem(item2);
        if ((stateItem != null) && !AuthorizationManager.IsAllowed(stateItem, AccessRight.WorkflowStateDelete, account))
        {
          flag = false;
          break;
        }
      }
      if (flag)
      {
        return new AccessResult(AccessPermission.Allow, new AccessExplanation(item, account, AccessRight.ItemDelete, "The workflow state definition item allows deletion through the '{0}' access right. ", new object[] { AccessRight.WorkflowStateDelete.Name }));
      }
      return new AccessResult(AccessPermission.Deny, new AccessExplanation(item, account, AccessRight.ItemDelete, "One version of the item is in a workflow state that does not allow deletion. To allow deletion, grant the '{0}' access right to the workflow state definition item.", new object[] { AccessRight.WorkflowStateDelete.Name }));
    }

    private AccessResult GetDeleteVersionAccessInformation(Item item, Account account)
    {
      Item stateItem = this.GetStateItem(item);
      if ((stateItem == null) || AuthorizationManager.IsAllowed(stateItem, AccessRight.WorkflowStateDelete, account))
      {
        return new AccessResult(AccessPermission.Allow, new AccessExplanation(item, account, AccessRight.ItemRemoveVersion, "The workflow state definition item allows deletion through the '{0}' access right. ", new object[] { AccessRight.WorkflowStateDelete.Name }));
      }
      return new AccessResult(AccessPermission.Deny, new AccessExplanation(item, account, AccessRight.ItemRemoveVersion, "One version of the item is in a workflow state that does not allow deletion. To allow deletion, grant the '{0}' access right to the workflow state definition item.", new object[] { AccessRight.WorkflowStateDelete.Name }));
    }

    public virtual WorkflowEvent[] GetHistory(Item item)
    {
      Assert.ArgumentNotNull(item, "item");
      return this.HistoryStore.GetHistory(item);
    }

    private string GetInitialState(Item item)
    {
      Item workflowItem = this.GetWorkflowItem();
      Assert.IsNotNull(workflowItem, "Could not get initial state for item: " + item.Paths.FullPath);
      string str = workflowItem["initial state"];
      if (str.Length == 0)
      {
        throw new WorkflowInitialStateMissingException(item, workflowItem.DisplayName);
      }
      return str;
    }

    public virtual int GetItemCount(string stateID)
    {
      Assert.ArgumentNotNull(stateID, "stateID");
      return this.GetItems(stateID).Length;
    }

    public virtual DataUri[] GetItems(string stateID)
    {
      Assert.ArgumentNotNullOrEmpty(stateID, "stateID");
      Assert.IsTrue(ID.IsID(stateID), "Invalid state ID: " + stateID);
      DataUri[] itemsInWorkflowState = this.Database.DataManager.GetItemsInWorkflowState(new WorkflowInfo(this.WorkflowID, stateID));
      if (itemsInWorkflowState != null)
      {
        return itemsInWorkflowState;
      }
      return new DataUri[0];
    }

    private ID GetNextStateId(Item commandItem)
    {
      string str = commandItem["next state"];
      if (str.Length == 0)
      {
        return null;
      }
      return MainUtil.GetID(str, null);
    }

    public void GetObjectData(SerializationInfo info, StreamingContext context)
    {
      info.AddValue("workflowid", this._workflowID);
      info.AddValue("database", this._owner.Database.Name);
    }

    public virtual WorkflowState GetState(Item item)
    {
      Assert.ArgumentNotNull(item, "item");
      Item stateItem = this.GetStateItem(item);
      if (stateItem != null)
      {
        return this.CreateWorkflowState(stateItem);
      }
      return null;
    }

    public virtual WorkflowState GetState(string stateID)
    {
      Assert.ArgumentNotNullOrEmpty(stateID, "stateID");
      Item stateItem = this.GetStateItem(stateID);
      if (stateItem != null)
      {
        return this.CreateWorkflowState(stateItem);
      }
      return null;
    }

    private string GetStateID(Item item)
    {
      Assert.ArgumentNotNull(item, "item");
      WorkflowInfo workflowInfo = item.Database.DataManager.GetWorkflowInfo(item);
      if (workflowInfo != null)
      {
        return workflowInfo.StateID;
      }
      return string.Empty;
    }

    protected virtual Item GetStateItem(ID stateId) =>
        ItemManager.GetItem(stateId, Language.Current, Sitecore.Data.Version.Latest, this.Database, SecurityCheck.Disable);

    protected virtual Item GetStateItem(Item item)
    {
      string stateID = this.GetStateID(item);
      if (stateID.Length > 0)
      {
        return this.GetStateItem(stateID);
      }
      return null;
    }

    protected virtual Item GetStateItem(string stateId)
    {
      ID iD = MainUtil.GetID(stateId, null);
      if (iD.IsNull)
      {
        return null;
      }
      return this.GetStateItem(iD);
    }

    private IEnumerable<string> GetStatePreviewPublishingTargetDatabases(Item state) =>
        (from target in this.GetStatePreviewPublishingTargetItems(state) select target[FieldIDs.PublishingTargetDatabase]);

    private IEnumerable<Item> GetStatePreviewPublishingTargetItems(Item state)
    {
      if (state == null)
      {
        return Enumerable.Empty<Item>();
      }
      MultilistField field = state.Fields[WorkflowFieldIDs.PreviewPublishingTargets];
      
      return (from t in field.GetItems()
              where t.Fields[PublishingTargetFieldIDs.PreviewPublishingTarget] != null
              where ((CheckboxField)t.Fields[PublishingTargetFieldIDs.PreviewPublishingTarget]).Checked
              select t);
    }

    private IEnumerable<string> GetStatePreviewPublishingTargets(Item state) =>
        (from target in this.GetStatePreviewPublishingTargetItems(state) select target.DisplayName).ToList<string>();

    public virtual WorkflowState[] GetStates()
    {
      Item workflowItem = this.GetWorkflowItem();
      if (workflowItem == null)
      {
        return new WorkflowState[0];
      }
      Item[] itemArray = workflowItem.Children.ToArray();
      WorkflowState[] stateArray = new WorkflowState[itemArray.Length];
      for (int i = 0; i < stateArray.Length; i++)
      {
        Item item = itemArray[i];
        stateArray[i] = this.CreateWorkflowState(item);
      }
      return stateArray;
    }

    private string GetWorkflowFromState(ID stateId)
    {
      Item stateItem = this.GetStateItem(stateId);
      if (stateItem != null)
      {
        Item parent = stateItem.Parent;
        if (parent != null)
        {
          return parent.ID.ToString();
        }
      }
      return this.WorkflowID;
    }

    protected virtual Item GetWorkflowItem() =>
        ItemManager.GetItem(this._workflowID, Language.Current, Sitecore.Data.Version.Latest, this.Database, SecurityCheck.Disable);

    private AccessResult GetWriteAccessInformation(Item item, Account account, Item stateItem)
    {
      if (AuthorizationManager.IsAllowed(stateItem, AccessRight.WorkflowStateWrite, account))
      {
        return new AccessResult(AccessPermission.Allow, new AccessExplanation(item, account, AccessRight.ItemWrite, "The workflow state definition item allows writing (through the '{0}' access right).", new object[] { AccessRight.WorkflowStateWrite.Name }));
      }
      return new AccessResult(AccessPermission.Deny, new AccessExplanation(item, account, AccessRight.ItemWrite, "The workflow state definition item does not allow writing. To allow writing, grant the '{0}' access right to the workflow state definition item.", new object[] { AccessRight.WorkflowStateWrite.Name }));
    }

    public virtual bool IsApproved(Item item) =>
        this.IsApproved(item, null);

    public virtual bool IsApproved(Item item, Sitecore.Data.Database targetDatabase)
    {
      Func<string, bool> predicate = null;
      Assert.ArgumentNotNull(item, "item");
      Item stateItem = this.GetStateItem(item);
      if (stateItem == null)
      {
        return true;
      }
      bool flag = stateItem[WorkflowFieldIDs.FinalState] == "1";
      if (flag || (targetDatabase == null))
      {
        return flag;
      }
      if (predicate == null)
      {
        predicate = target => targetDatabase.Name.Equals(target);
      }
      return this.GetStatePreviewPublishingTargetDatabases(stateItem).Any<string>(predicate);
    }

    protected virtual void PerformTransition(Item commandItem, Item item, ID stateId, StringDictionary commentFields)
    {
      this.SetStateID(item, stateId.ToString(), commentFields, this.GetWorkflowFromState(stateId));
      DataCount.WorkflowStateChanges.Increment(1L);
    }

    private void SetStateID(Item item, string stateID, StringDictionary commentFields)
    {
      this.SetStateID(item, stateID, commentFields, this.WorkflowID);
    }

    private void SetStateID(Item item, string stateID, StringDictionary commentFields, string workflowID)
    {
      using (new SecurityDisabler())
      {
        string oldState = this.GetStateID(item);
        if (oldState != stateID)
        {
          WorkflowInfo info = new WorkflowInfo(workflowID, stateID);
          item.Database.DataManager.SetWorkflowInfo(item, info);
          this.AddHistory(item, oldState, stateID, commentFields);
        }
      }
    }

    public virtual void Start(Item item)
    {
      Assert.ArgumentNotNull(item, "item");
      this.ClearHistory(item);
      string initialState = this.GetInitialState(item);
      string str2 = Translate.Text("Item created");
      StringDictionary commentFields = new StringDictionary();
      commentFields.Add("Comments", str2);
      this.SetStateID(item, initialState, commentFields);
      Item stateItem = this.GetStateItem(initialState);
      if (stateItem != null)
      {
        StringDictionary dictionary = new StringDictionary();
        dictionary.Add("Comments", str2);
        this.ExecuteStateActions(stateItem, item, dictionary, new object[0]);
      }
    }

    public void StartExecute(string commandID, Item item, StringDictionary commentFields, bool allowUI, Action completionCallback, params object[] parameters)
    {
      Processor callback = null;
      if (completionCallback != null)
      {
        ActionProcessorWrapper wrapper = new ActionProcessorWrapper(completionCallback);
        callback = new Processor("Callback Processor", wrapper, "Invoke");
      }
      this.Execute(commandID, item, commentFields, allowUI, callback, parameters);
    }

    public virtual Sitecore.Data.Appearance Appearance
    {
      get
      {
        Item workflowItem = this.GetWorkflowItem();
        if (workflowItem != null)
        {
          return new Sitecore.Data.Appearance(workflowItem.DisplayName) { Icon = workflowItem.Appearance.Icon };
        }
        return new Sitecore.Data.Appearance();
      }
    }

    protected Sitecore.Data.Database Database =>
        this._owner.Database;

    protected Sitecore.Workflows.HistoryStore HistoryStore =>
        this._owner.HistoryStore;

    public virtual string WorkflowID =>
        this._workflowID.ToString();

    [Serializable]
    private class ActionProcessorWrapper : ISerializable
    {
      private readonly Action _action;
      private readonly Guid uid;

      public ActionProcessorWrapper(Action action)
      {
        this.uid = Guid.NewGuid();
        this._action = action;
      }

      protected ActionProcessorWrapper(SerializationInfo info, StreamingContext context)
      {
        Assert.ArgumentNotNull(info, "info");
        this.uid = (Guid)info.GetValue("uid", typeof(Guid));
        string callbackId = (string)info.GetValue("completionCallbackid", typeof(string));
        this._action = this.RestoreCallbackAction(callbackId);
      }

      private string GenerateKey(Guid id) =>
          $"{id}_{WebUtil.GetSessionID()}";

      public void GetObjectData(SerializationInfo info, StreamingContext context)
      {
        Assert.ArgumentNotNull(info, "info");
        info.AddValue("completionCallbackid", this.PreserveCallbackAction());
        info.AddValue("uid", this.uid);
      }

      public void Invoke(WorkflowPipelineArgs args)
      {
        if (this._action != null)
        {
          this._action();
        }
      }

      private string PreserveCallbackAction()
      {
        Dictionary<string, Action> dictionary = (Dictionary<string, Action>)HttpContext.Current.Application["WorkflowCallbackActions"];
        if (dictionary == null)
        {
          dictionary = new Dictionary<string, Action>();
          HttpContext.Current.Application.Add("WorkflowCallbackActions", dictionary);
        }
        string key = this.GenerateKey(this.uid);
        if (!dictionary.ContainsKey(key))
        {
          dictionary.Add(key, this._action);
        }
        return key;
      }

      private Action RestoreCallbackAction(string callbackId)
      {
        Action action = null;
        if (HttpContext.Current == null)
        {
          return null;
        }
        Dictionary<string, Action> dictionary = (Dictionary<string, Action>)HttpContext.Current.Application["WorkflowCallbackActions"];
        if (dictionary == null)
        {
          return null;
        }
        if (string.IsNullOrEmpty(callbackId))
        {
          return null;
        }
        if (dictionary.ContainsKey(callbackId))
        {
          action = dictionary[callbackId];
        }
        return action;
      }
    }
  }
}
